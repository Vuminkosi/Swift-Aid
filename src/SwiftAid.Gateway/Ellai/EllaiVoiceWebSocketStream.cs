using System.Net.WebSockets;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Validates segmented packets without filling intentional gaps with invented PCM.</summary>
internal sealed class EllaiVoiceWebSocketStream(WebSocket socket, long initialPosition = 0) : Stream
{
    private readonly byte[] _packet = new byte[EllaiSpeechEvent.HeaderBytes + EllaiSpeechEvent.MaximumPcmBytes];
    private int _remaining, _cursor;
    public bool SegmentEnded { get; private set; }
    public bool SocketClosed { get; private set; }
    public long SegmentStartPosition => _packetStart;
    private long _sequence, _epoch, _audioEnd = initialPosition;
    private readonly long _initialPosition = initialPosition;
    private long _packetStart;
    private long _observedPosition = initialPosition;
    public long ReceivedBytes { get; private set; }
    public long SuppressedSamples => Math.Max(0, _observedPosition - _initialPosition - ReceivedBytes / 2);
    public long DuplicateEvents { get; private set; }
    public long MissingEvents { get; private set; }
    public long LastReadSamplePosition { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0 || SegmentEnded || SocketClosed) return 0;
        while (_remaining == 0)
        {
            var size = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (size == _packet.Length) throw new InvalidDataException("Voice packet is too large.");
                result = await socket.ReceiveAsync(_packet.AsMemory(size), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (size != 0) throw new InvalidDataException("Incomplete voice packet.");
                    SocketClosed = true;
                    return 0;
                }
                if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Expected a voice packet.");
                size += result.Count;
            } while (!result.EndOfMessage);
            var header = EllaiSpeechEvent.Decode(_packet.AsSpan(0, size));
            if (_epoch == 0) _epoch = header.StreamEpoch;
            if (header.StreamEpoch != _epoch) throw new InvalidDataException("Stale voice epoch.");
            if (header.Sequence <= _sequence) { DuplicateEvents++; continue; }
            if (header.Sequence != _sequence + 1)
            {
                MissingEvents++;
                throw new InvalidDataException("Missing voice event; capture must recover.");
            }
            _sequence = header.Sequence;
            _observedPosition = Math.Max(_observedPosition, header.SamplePosition);
            if (header.Kind == EllaiSpeechEventKind.SpeechEnded) { SegmentEnded = true; return 0; }
            if (header.Kind != EllaiSpeechEventKind.Audio) continue;
            if (header.SamplePosition < _audioEnd) throw new InvalidDataException("Overlapping voice audio.");
            _packetStart = header.SamplePosition;
            _remaining = size - EllaiSpeechEvent.HeaderBytes;
            _cursor = EllaiSpeechEvent.HeaderBytes;
            _audioEnd = _packetStart + _remaining / 2;
            ReceivedBytes += _remaining;
            _observedPosition = Math.Max(_observedPosition, _audioEnd);
            if (ReceivedBytes > 32 * 1024 * 1024) throw new InvalidDataException("Voice audio limit exceeded.");
        }
        var count = Math.Min(buffer.Length & ~1, _remaining);
        if (count == 0) throw new ArgumentException("PCM reads require at least two bytes.");
        LastReadSamplePosition = _packetStart + (_cursor - EllaiSpeechEvent.HeaderBytes) / 2;
        _packet.AsMemory(_cursor, count).CopyTo(buffer);
        _cursor += count;
        _remaining -= count;
        return count;
    }
    // Delay creating a provider request until real PCM is available. EOF here
    // terminates only the current provider request, never the parent socket.
    public async Task<bool> WaitForSegmentAsync(CancellationToken cancellationToken)
    {
        SegmentEnded = false;
        var probe = new byte[2];
        while (!SocketClosed)
        {
            var count = await ReadAsync(probe, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                _cursor -= count;
                _remaining += count;
                Array.Clear(probe);
                return true;
            }
            SegmentEnded = false;
        }
        return false;
    }

    protected override void Dispose(bool disposing) { Array.Clear(_packet); base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
