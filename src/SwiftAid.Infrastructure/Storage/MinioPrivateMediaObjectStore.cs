using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Attachments;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Storage;

/// <summary>
/// Reads private MinIO objects by opaque reference. This adapter has no knowledge of cases,
/// actors, authorization grants, or public object URLs.
/// </summary>
public sealed class MinioPrivateMediaObjectStore : IPrivateMediaObjectStore
{
    private const int ChunkSizeBytes = 64 * 1024;
    private const int BufferedChunkCount = 2;

    private readonly IMinioClient _client;
    private readonly MinioOptions _options;
    private readonly ILogger<MinioPrivateMediaObjectStore> _logger;

    public MinioPrivateMediaObjectStore(
        IMinioClient client,
        IOptions<MinioOptions> options,
        ILogger<MinioPrivateMediaObjectStore> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<AttachmentRangeContent?> OpenReadAsync(
        ObjectReference reference,
        AttachmentByteRange range,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Bucket) || _options.MaxObjectSizeBytes <= 0)
        {
            throw new InvalidOperationException("Private media object storage is not configured.");
        }

        long rangeLength;
        try
        {
            rangeLength = range.Length;
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range,
                "The requested byte range is too large.");
        }

        if (rangeLength > _options.MaxObjectSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The requested byte range exceeds the configured object limit.");
        }

        ObjectStat stat;
        try
        {
            stat = await _client.StatObjectAsync(
                new StatObjectArgs()
                    .WithBucket(_options.Bucket)
                    .WithObject(ToStorageKey(reference)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(
                "Private media metadata read failed. Failure type: {FailureType}",
                exception.GetType().Name);
            throw;
        }

        if (stat.Size <= 0 || stat.Size > _options.MaxObjectSizeBytes)
        {
            throw new InvalidDataException("The private media object has an invalid configured size.");
        }

        if (range.EndInclusive >= stat.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The requested byte range exceeds the object length.");
        }

        var stream = new BoundedRangeReadStream(
            rangeLength,
            cancellationToken,
            (producerToken, destination) =>
                FetchRangeAsync(reference, range, rangeLength, stat, producerToken, destination));

        try
        {
            return new AttachmentRangeContent(
                stream,
                string.IsNullOrWhiteSpace(stat.ContentType) ? "application/octet-stream" : stat.ContentType,
                range,
                stat.Size);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task FetchRangeAsync(
        ObjectReference reference,
        AttachmentByteRange range,
        long rangeLength,
        ObjectStat stat,
        CancellationToken producerToken,
        BoundedRangeReadStream destination)
    {
        var args = new GetObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(ToStorageKey(reference))
            .WithOffsetAndLength(range.Start, rangeLength)
            .WithCallbackStream((source, sdkCancellationToken) =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    producerToken,
                    sdkCancellationToken);
                return destination.WriteExactRangeAsync(source, rangeLength, linkedCancellation.Token);
            });

        if (!string.IsNullOrWhiteSpace(stat.ETag))
        {
            args.WithMatchETag(stat.ETag);
        }

        try
        {
            await _client.GetObjectAsync(args, producerToken).ConfigureAwait(false);
        }
        catch (ObjectNotFoundException)
        {
            throw new FileNotFoundException("The private media object disappeared during the range read.");
        }
        catch (OperationCanceledException) when (producerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(
                "Private media range read failed. Failure type: {FailureType}",
                exception.GetType().Name);
            throw;
        }
    }

    private static string ToStorageKey(ObjectReference reference) => reference.Id.ToString("N");

    private sealed class BoundedRangeReadStream : Stream
    {
        private readonly long _expectedLength;
        private readonly Func<CancellationToken, BoundedRangeReadStream, Task> _produce;
        private readonly Channel<byte[]> _chunks;
        private readonly CancellationTokenSource _transferCancellation;
        private readonly Task _producerTask;
        private readonly object _disposeLock = new();

        private byte[]? _currentChunk;
        private int _currentChunkOffset;
        private long _bytesRead;
        private Exception? _producerFailure;
        private bool _disposed;
        private Task? _disposeTask;

        public BoundedRangeReadStream(
            long expectedLength,
            CancellationToken cancellationToken,
            Func<CancellationToken, BoundedRangeReadStream, Task> produce)
        {
            _expectedLength = expectedLength;
            _produce = produce;
            _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(BufferedChunkCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            _producerTask = ProduceAsync();
        }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BoundedRangeReadStream));
            }

            if (buffer.Length == 0)
            {
                return ValueTask.FromResult(0);
            }

            return ReadCoreAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask DisposeAsync()
        {
            await StartDispose().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _ = StartDispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public async Task WriteExactRangeAsync(
            Stream source,
            long rangeLength,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[ChunkSizeBytes];
            var remaining = rangeLength;

            while (remaining > 0)
            {
                var requestedCount = (int)Math.Min(buffer.Length, remaining);
                var count = await source.ReadAsync(
                    buffer.AsMemory(0, requestedCount),
                    cancellationToken).ConfigureAwait(false);

                if (count == 0)
                {
                    throw new InvalidDataException("The private media provider returned a short byte range.");
                }

                if (count < 0 || count > requestedCount)
                {
                    throw new InvalidDataException("The private media provider returned an invalid byte count.");
                }

                var chunk = buffer.AsSpan(0, count).ToArray();
                await _chunks.Writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                remaining -= count;
            }

            var overflowProbe = new byte[1];
            var overflowCount = await source.ReadAsync(overflowProbe.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (overflowCount != 0)
            {
                throw new InvalidDataException("The private media provider returned bytes beyond the requested range.");
            }
        }

        private async Task ProduceAsync()
        {
            try
            {
                await _produce(_transferCancellation.Token, this).ConfigureAwait(false);
                _chunks.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                _producerFailure = exception;
                _chunks.Writer.TryComplete(exception);
            }
        }

        private Task StartDispose()
        {
            lock (_disposeLock)
            {
                if (_disposeTask is not null)
                {
                    return _disposeTask;
                }

                _disposed = true;
                _transferCancellation.Cancel();
                _chunks.Writer.TryComplete();
                _disposeTask = DisposeAfterProducerAsync();
                return _disposeTask;
            }
        }

        private async Task DisposeAfterProducerAsync()
        {
            try
            {
                await _producerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposing the response stream cancels the provider transfer.
            }
            finally
            {
                _transferCancellation.Dispose();
            }
        }

        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _transferCancellation.Token);
            var readToken = linkedCancellation.Token;
            readToken.ThrowIfCancellationRequested();

            if (_bytesRead == _expectedLength)
            {
                await _producerTask.WaitAsync(readToken).ConfigureAwait(false);
                ThrowProducerFailure();
                return 0;
            }

            while (_currentChunk is null || _currentChunkOffset == _currentChunk.Length)
            {
                _currentChunk = null;
                _currentChunkOffset = 0;

                try
                {
                    _currentChunk = await _chunks.Reader.ReadAsync(readToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    ThrowProducerFailure();
                    if (_bytesRead != _expectedLength)
                    {
                        throw new InvalidDataException("The private media provider returned a short byte range.");
                    }

                    return 0;
                }
            }

            var bytesAvailable = _currentChunk.Length - _currentChunkOffset;
            var bytesToCopy = (int)Math.Min(Math.Min(buffer.Length, bytesAvailable), _expectedLength - _bytesRead);
            _currentChunk.AsMemory(_currentChunkOffset, bytesToCopy).CopyTo(buffer);
            _currentChunkOffset += bytesToCopy;
            _bytesRead += bytesToCopy;
            return bytesToCopy;
        }

        private void ThrowProducerFailure()
        {
            if (_producerFailure is not null)
            {
                ExceptionDispatchInfo.Capture(_producerFailure).Throw();
            }
        }
    }
}
