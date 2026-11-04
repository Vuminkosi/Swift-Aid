using SwiftAid.Application.Abstractions;

namespace SwiftAid.Infrastructure.Storage;

public sealed class NotConfiguredObjectStorage : IObjectStorage
{
    public ValueTask<ObjectStorageResult> StoreAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new ObjectStorageResult(ObjectStorageStatus.Cancelled)
            : new ObjectStorageResult(ObjectStorageStatus.NotConfigured));

    public ValueTask<ObjectStorageResult> CopyToAsync(
        ObjectReference reference,
        Stream destination,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new ObjectStorageResult(ObjectStorageStatus.Cancelled)
            : new ObjectStorageResult(ObjectStorageStatus.NotConfigured));
}
