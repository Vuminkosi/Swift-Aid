using SwiftAid.Application.Intake.Imports;

namespace SwiftAid.Gateway.Ingestion;

/// <summary>Enforces the wire-byte limit even when the HTTP request has no Content-Length.</summary>
internal sealed class CaseImportRequestStream(Stream inner, long maximumBytes) : Stream
{
    private long _bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, Limit(count));
        Check(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken).ConfigureAwait(false);
        Check(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Limit(int requested) => (int)Math.Min(requested, Math.Max(1, maximumBytes - _bytesRead + 1));

    private void Check(int read)
    {
        _bytesRead += read;
        if (_bytesRead > maximumBytes) throw new CaseImportException("batch-byte-limit");
    }

    // The HTTP server owns the underlying request stream.
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
