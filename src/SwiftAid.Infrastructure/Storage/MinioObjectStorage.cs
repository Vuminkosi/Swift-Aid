using System.Net.Http.Headers;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Storage;

public sealed class MinioObjectStorage : IObjectStorage
{
    private const long MinioUnknownObjectSize = -1;

    private readonly IMinioClient _client;
    private readonly MinioOptions _options;
    private readonly ILogger<MinioObjectStorage> _logger;

    public MinioObjectStorage(
        IMinioClient client,
        IOptions<MinioOptions> options,
        ILogger<MinioObjectStorage> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<ObjectStorageResult> StoreAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || !MediaTypeHeaderValue.TryParse(contentType, out _))
        {
            return new ObjectStorageResult(ObjectStorageStatus.Failed, FailureCode: "invalid-input");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ObjectStorageResult(ObjectStorageStatus.Cancelled);
        }

        if (content.CanSeek &&
            (content.Length < content.Position || content.Length - content.Position > _options.MaxObjectSizeBytes))
        {
            return new ObjectStorageResult(ObjectStorageStatus.Failed, FailureCode: "object-too-large");
        }

        var reference = new ObjectReference(Guid.NewGuid());
        await using var limitedContent = new SizeLimitedReadStream(content, _options.MaxObjectSizeBytes);

        try
        {
            var objectSize = limitedContent.CanSeek
                ? limitedContent.Length - limitedContent.Position
                : MinioUnknownObjectSize;
            var args = new PutObjectArgs()
                .WithBucket(_options.Bucket)
                .WithObject(ToStorageKey(reference))
                .WithStreamData(limitedContent)
                .WithObjectSize(objectSize)
                .WithContentType(contentType);

            await _client.PutObjectAsync(args, cancellationToken).ConfigureAwait(false);
            return new ObjectStorageResult(ObjectStorageStatus.Succeeded, reference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ObjectStorageResult(ObjectStorageStatus.Cancelled);
        }
        catch (ObjectTooLargeException)
        {
            _logger.LogWarning("Object storage rejected an upload that exceeded the configured size limit.");
            return new ObjectStorageResult(ObjectStorageStatus.Failed, FailureCode: "object-too-large");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(
                "Object storage upload failed. Failure type: {FailureType}",
                exception.GetType().Name);
            return new ObjectStorageResult(ObjectStorageStatus.Failed, FailureCode: "object-storage-failure");
        }
    }

    public async ValueTask<ObjectStorageResult> CopyToAsync(
        ObjectReference reference,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            return new ObjectStorageResult(ObjectStorageStatus.Failed, FailureCode: "invalid-destination");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ObjectStorageResult(ObjectStorageStatus.Cancelled);
        }

        try
        {
            var args = new GetObjectArgs()
                .WithBucket(_options.Bucket)
                .WithObject(ToStorageKey(reference))
                .WithCallbackStream((stream, sdkCancellationToken) =>
                    CopyBoundedAsync(stream, destination, cancellationToken, sdkCancellationToken));

            await _client.GetObjectAsync(args, cancellationToken).ConfigureAwait(false);
            return new ObjectStorageResult(ObjectStorageStatus.Succeeded, reference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ObjectStorageResult(ObjectStorageStatus.Cancelled);
        }
        catch (ObjectNotFoundException)
        {
            return new ObjectStorageResult(ObjectStorageStatus.NotFound);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(
                "Object storage read failed. Failure type: {FailureType}",
                exception.GetType().Name);
            return new ObjectStorageResult(ObjectStorageStatus.Failed, FailureCode: "object-storage-failure");
        }
    }

    private static string ToStorageKey(ObjectReference reference) => reference.Id.ToString("N");

    private async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken,
        CancellationToken sdkCancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            sdkCancellationToken);
        await using var limitedSource = new SizeLimitedReadStream(source, _options.MaxObjectSizeBytes);
        await limitedSource.CopyToAsync(destination, linkedCancellation.Token).ConfigureAwait(false);
    }

    private sealed class SizeLimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;

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
            var limit = LimitReadCount(count);
            var read = inner.Read(buffer, offset, limit);
            RecordRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var limit = LimitReadCount(buffer.Length);
            var read = await inner.ReadAsync(buffer[..limit], cancellationToken).ConfigureAwait(false);
            RecordRead(read);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadArrayAsync(buffer, offset, count, cancellationToken);

        private async Task<int> ReadArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var limit = LimitReadCount(count);
            var read = await inner.ReadAsync(buffer, offset, limit, cancellationToken).ConfigureAwait(false);
            RecordRead(read);
            return read;
        }

        private int LimitReadCount(int requestedCount)
        {
            if (requestedCount <= 0)
            {
                return 0;
            }

            var remaining = maximumBytes - _bytesRead;
            if (remaining < 0)
            {
                throw new ObjectTooLargeException();
            }

            var maxRead = remaining == long.MaxValue ? int.MaxValue : (int)Math.Min(remaining + 1, int.MaxValue);
            return Math.Min(requestedCount, maxRead);
        }

        private void RecordRead(int count)
        {
            _bytesRead += count;
            if (_bytesRead > maximumBytes)
            {
                throw new ObjectTooLargeException();
            }
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The caller owns the input stream. The SDK callback owns callback streams.
            base.Dispose(disposing);
        }
    }

    private sealed class ObjectTooLargeException : InvalidOperationException
    {
    }
}
