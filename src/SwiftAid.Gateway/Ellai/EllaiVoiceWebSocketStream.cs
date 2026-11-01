using System.Net.WebSockets;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Exposes one client WebSocket's binary PCM messages as a bounded
/// sequential stream for the existing realtime transcription pipeline.</summary>
internal sealed class EllaiVoiceWebSocketStream(WebSocket socket) : Stream
{
    private const int MaximumMessageBytes = 32 * 1024 * 1024;
    private long _receivedBytes;
    public long ReceivedBytes => _receivedBytes;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return 0;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new InvalidDataException("Voice WebSocket messages must contain binary PCM.");
            }

            _receivedBytes += result.Count;
            if (_receivedBytes > MaximumMessageBytes)
            {
                throw new InvalidDataException("Voice WebSocket audio exceeded the configured limit.");
            }

            if (result.Count > 0)
            {
                return result.Count;
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
