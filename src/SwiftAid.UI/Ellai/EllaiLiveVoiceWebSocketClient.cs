using System.Net.WebSockets;

namespace SwiftAid.UI.Ellai;

/// <summary>Native PCM transport through the same endpoint as the browser. Capture owns the input stream.</summary>
public static class EllaiLiveVoiceWebSocketClient
{
    public static async Task SendAsync(HttpClient http, Uri destination, Stream audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(audio);
        var origin = http.BaseAddress ?? throw new InvalidOperationException("Gateway address is unavailable.");
        var expectedScheme = origin.Scheme == "https" ? "wss" : "ws";
        if (origin.Scheme is not ("http" or "https") || destination.Scheme != expectedScheme ||
            !destination.Authority.Equals(origin.Authority, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Voice must connect to the configured Gateway.");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin.GetLeftPart(UriPartial.Authority));
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var handshake = new HttpMessageInvoker(new GatewayHandshakeHandler(http));
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            // Reuse the existing cookie/bearer handlers; never create a second credential store.
            await socket.ConnectAsync(destination, handshake, connect.Token).ConfigureAwait(false);
        }

        var inputEnded = false;
        async Task SendAudioAsync()
        {
            var buffer = new byte[8192];
            var carry = 0;
            try
            {
                while (true)
                {
                    var read = await audio.ReadAsync(buffer.AsMemory(carry), lifetime.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    var available = carry + read;
                    var count = available & ~1;
                    if (count > 0)
                    {
                        using var send = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        send.CancelAfter(TimeSpan.FromSeconds(5));
                        await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, send.Token).ConfigureAwait(false);
                    }
                    carry = available - count;
                    if (carry != 0) buffer[0] = buffer[count];
                }
                if (carry != 0) throw new InvalidDataException("PCM capture ended with an incomplete sample.");
                inputEnded = true;
                lifetime.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Capture stopped", lifetime.Token).ConfigureAwait(false);
            }
            finally { Array.Clear(buffer); }
        }

        async Task ReceiveCloseAsync()
        {
            var buffer = new byte[256];
            var result = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Close || !inputEnded ||
                socket.CloseStatus != WebSocketCloseStatus.NormalClosure)
                throw new WebSocketException("Gateway voice connection ended before capture completed.");
        }

        var receiver = ReceiveCloseAsync();
        var sender = SendAudioAsync();
        try
        {
            var first = await Task.WhenAny(sender, receiver).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            await Task.WhenAll(sender, receiver).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            socket.Abort();
            try { await Task.WhenAll(sender, receiver).ConfigureAwait(false); }
            catch { /* Observe both tasks without masking the original failure. */ }
        }
    }

    private sealed class GatewayHandshakeHandler(HttpClient http) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The WebSocket API supplies ws/wss; existing origin and credential handlers
            // validate the equivalent HTTP upgrade request. Do not buffer its duplex body.
            var uri = request.RequestUri!;
            request.RequestUri = new UriBuilder(uri) { Scheme = uri.Scheme == "wss" ? "https" : "http" }.Uri;
            return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
    }
}
