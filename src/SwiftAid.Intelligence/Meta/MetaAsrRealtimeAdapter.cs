using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Realtime session result: provider session id plus finalized transcripts.</summary>
public sealed record RealtimeAsrResult(string SessionId, IReadOnlyList<string> FinalTranscripts);

/// <summary>Live Meta realtime transcription adapter (ELL-T11). Protocol per
/// the official voice reference: connect, send the handshake as the first
/// JSON text frame within 10 s (credential travels in the handshake — the
/// Authorization header is ignored), stream raw PCM as binary frames, send
/// {"type":"endStream"} and keep reading until close 1000. Partial frames
/// never execute; only final transcripts are returned. Key never logged.</summary>
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
        Stream pcm24kMono16Bit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcm24kMono16Bit);
        var handshake = BuildHandshake();

        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await socket.ConnectAsync(new Uri(options.AsrWebSocketUrl), linked.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Ellai realtime ASR connect failed ({ex.GetType().Name}).");
        }

        await SendTextAsync(socket, handshake, linked.Token);
        var sessionId = await ReadHandshakeAckAsync(socket, linked.Token);

        var finals = new List<string>();
        var receiver = ReceiveUntilCloseAsync(socket, finals, linked.Token);

        var buffer = new byte[7680];
        int read;
        while ((read = await pcm24kMono16Bit.ReadAsync(buffer, linked.Token)) > 0)
        {
            await socket.SendAsync(new ArraySegment<byte>(buffer, 0, read),
                WebSocketMessageType.Binary, true, linked.Token);
        }

        await SendTextAsync(socket, """{"type":"endStream"}""", linked.Token);
        await receiver;
        return new RealtimeAsrResult(sessionId, finals);
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
        ClientWebSocket socket, List<string> finals, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open)
        {
            string? text = null;
            try
            {
                text = await ReceiveTextAsync(socket, CancellationToken.None);
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

                if (root.TryGetProperty("type", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == "transcript"
                    && root.TryGetProperty("final", out var fin)
                    && fin.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("transcript", out var tr)
                    && tr.ValueKind == JsonValueKind.String)
                {
                    finals.Add(tr.GetString() ?? string.Empty);
                }
            }
            catch (JsonException)
            {
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
