using System.Net.WebSockets;
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
        using var handshakeLinked = CancellationTokenSource.CreateLinkedTokenSource(
            linked.Token, handshakeTimeout.Token);

        try
        {
            await socket.ConnectAsync(new Uri(options.AsrWebSocketUrl), handshakeLinked.Token);
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

        var buffer = new byte[7680];
        int read;
        while ((read = await pcm24kMono16Bit.ReadAsync(buffer, linked.Token)) > 0)
        {
            if (onAudioChunk is not null)
            {
                await onAudioChunk(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
            }

            await socket.SendAsync(new ArraySegment<byte>(buffer, 0, read),
                WebSocketMessageType.Binary, true, linked.Token);
        }

        await SendTextAsync(socket, """{"type":"endStream"}""", linked.Token);
        await receiver;
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
            string? text = null;
            try
            {
                text = await ReceiveTextAsync(socket, ct);
            }
            catch (WebSocketException)
            {
                break;
            }

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
                    var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() : "provider error";
                    throw new InvalidOperationException($"Ellai realtime ASR error: {message}");
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
