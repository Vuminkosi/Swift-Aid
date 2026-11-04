using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Realtime session result: provider session id plus finalized transcripts.</summary>
public sealed record RealtimeAsrResult(string SessionId, IReadOnlyList<MetaAsrRealtimeTranscript> Turns)
{
    public IReadOnlyList<string> FinalTranscripts => Turns.Select(turn => turn.Text).ToArray();
}

/// <summary>Live Meta realtime transcription adapter (ELL-T11). Protocol per
/// the official voice reference: connect, send the handshake as the first
/// JSON text frame within 10 s (credential travels in the handshake — the
/// Authorization header is ignored), stream raw PCM as binary frames, send
/// {"type":"endStream"} and keep reading until close 1000. Partial frames
/// are forwarded to an optional display-only callback; only provider-final
/// transcripts are returned. Key never logged.</summary>
public sealed class MetaAsrRealtimeAdapter(EllaiOptions options)
{
    public const int HandshakeTimeoutSeconds = 10;

    public string BuildHandshake()
    {
        if (!options.HasRealtimeAsrRoute)
        {
            throw new MetaConfigurationException("Ellai realtime ASR route is not configured.");
        }

        return JsonSerializer.Serialize(new
        {
            authorization = new { accessToken = "Bearer " + options.ApiKey },
            audioEncoding = "PCM_24KHZ",
            model = options.AsrModel,
            mode = options.AsrMode,
        });
    }

    public async Task<RealtimeAsrResult> TranscribePcmAsync(
        Stream pcm24kMono16Bit,
        CancellationToken ct,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? onAudioChunk = null,
        Func<MetaAsrRealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
        Func<MetaAsrRealtimeTranscript, CancellationToken, ValueTask>? onFinalTranscript = null)
    {
        ArgumentNullException.ThrowIfNull(pcm24kMono16Bit);
        var handshake = BuildHandshake();

        using var socket = new ClientWebSocket();
        // The Windows SChannel path repeatedly failed receiving this provider's
        // WebSocket stream with default TLS negotiation; TLS 1.2 succeeds.
        using var windowsTransport = OperatingSystem.IsWindows()
            ? new HttpMessageInvoker(new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12
                }
            })
            : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
        using var handshakeLinked = CancellationTokenSource.CreateLinkedTokenSource(
            linked.Token, handshakeTimeout.Token);

        try
        {
            if (windowsTransport is null)
                await socket.ConnectAsync(new Uri(options.AsrWebSocketUrl), handshakeLinked.Token);
            else
                await socket.ConnectAsync(new Uri(options.AsrWebSocketUrl), windowsTransport, handshakeLinked.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Ellai realtime ASR connect failed ({ex.GetType().Name}).");
        }

        await SendTextAsync(socket, handshake, handshakeLinked.Token);
        var sessionId = await ReadHandshakeAckAsync(socket, handshakeLinked.Token);

        var transcripts = new MetaAsrRealtimeTranscriptCollector();
        var receiver = ReceiveUntilCloseAsync(
            socket, transcripts, onPartialTranscript, onFinalTranscript, linked.Token);

        var inputCompleted = false;
        async Task SendAudioAsync()
        {
            var buffer = new byte[7680];
            var audioClock = System.Diagnostics.Stopwatch.StartNew();
            long sentBytes = 0;
            try
            {
                int read;
                while ((read = await pcm24kMono16Bit.ReadAsync(buffer, linked.Token)) > 0)
                {
                    if (onAudioChunk is not null)
                    {
                        await onAudioChunk(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    }
                    await socket.SendAsync(new ArraySegment<byte>(buffer, 0, read),
                        WebSocketMessageType.Binary, true, linked.Token).ConfigureAwait(false);
                    // Buffered microphone frames and prerecorded streams must not burst
                    // faster than realtime into the provider's bounded streaming input.
                    sentBytes += read;
                    var remaining = TimeSpan.FromSeconds(sentBytes / 48000d) - audioClock.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, linked.Token).ConfigureAwait(false);
                }
                inputCompleted = true;
                await SendTextAsync(socket, """{"type":"endStream"}""", linked.Token).ConfigureAwait(false);
            }
            finally
            {
                Array.Clear(buffer);
            }
        }

        var sender = SendAudioAsync();
        try
        {
            var first = await Task.WhenAny(sender, receiver).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (first == receiver && !inputCompleted)
            {
                throw new InvalidOperationException("Ellai realtime ASR closed before the transcription request completed.");
            }
            await Task.WhenAll(sender, receiver).ConfigureAwait(false);
        }
        finally
        {
            // A failed receiver must release a sender waiting on an open microphone stream.
            // This request owns the linked cancellation, never the parent capture session.
            await linked.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(sender, receiver).ConfigureAwait(false); }
            catch { /* Both tasks are observed; preserve the primary failure above. */ }
        }
        return new RealtimeAsrResult(sessionId, transcripts.Turns);
    }

    private static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReadHandshakeAckAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var frame = await ReceiveTextAsync(socket, ct);
        if (frame is null)
        {
            throw new InvalidOperationException("Ellai realtime ASR closed before acknowledging.");
        }

        try
        {
            using var doc = JsonDocument.Parse(frame);
            if (doc.RootElement.TryGetProperty("sessionId", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException("Ellai realtime ASR handshake was not acknowledged.");
    }

    private static async Task ReceiveUntilCloseAsync(
        ClientWebSocket socket,
        MetaAsrRealtimeTranscriptCollector transcripts,
        Func<MetaAsrRealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript,
        Func<MetaAsrRealtimeTranscript, CancellationToken, ValueTask>? onFinalTranscript,
        CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open)
        {
            var text = await ReceiveTextAsync(socket, ct).ConfigureAwait(false);

            if (text is null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "error")
                {
                    throw new InvalidOperationException("Ellai realtime ASR rejected the transcription stream.");
                }

            }
            catch (JsonException)
            {
            }

            if (onPartialTranscript is not null
                && transcripts.TryReadPartial(text, out var partial)
                && partial is not null)
            {
                try
                {
                    await onPartialTranscript(partial, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Display-only partials must not interrupt provider audio or final transcription.
                }
            }
            else if (transcripts.TryAppend(text, out var finalTranscript)
                && finalTranscript is not null)
            {
                if (onFinalTranscript is not null)
                {
                    try
                    {
                        await onFinalTranscript(finalTranscript, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Turn notification is advisory to the caller; keep consuming provider output.
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var collected = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None); }
                catch (WebSocketException) { }
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                return null;
            }

            collected.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(collected.ToArray());
    }
}
