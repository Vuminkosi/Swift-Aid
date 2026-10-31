using System.Threading.Channels;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Read-only stream over bounded PCM chunks produced by microphone capture.</summary>
internal sealed class PcmChannelReadStream(ChannelReader<byte[]> reader) : Stream
{
    private byte[]? _currentChunk;
    private int _currentOffset;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (_currentChunk is null || _currentOffset == _currentChunk.Length)
        {
            ClearCurrentChunk();
            if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                || !reader.TryRead(out _currentChunk))
            {
                return 0;
            }
        }

        var count = Math.Min(buffer.Length, _currentChunk.Length - _currentOffset);
        _currentChunk.AsMemory(_currentOffset, count).CopyTo(buffer);
        _currentOffset += count;
        return count;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ClearCurrentChunk();
            while (reader.TryRead(out var chunk))
            {
                Array.Clear(chunk);
            }
        }

        base.Dispose(disposing);
    }

    private void ClearCurrentChunk()
    {
        if (_currentChunk is not null)
        {
            Array.Clear(_currentChunk);
            _currentChunk = null;
            _currentOffset = 0;
        }
    }
}
