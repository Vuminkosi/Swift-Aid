namespace SwiftAid.Application.Abstractions;

public enum ObjectStorageStatus
{
    Succeeded,
    NotFound,
    NotConfigured,
    Failed,
    Cancelled
}

public sealed record ObjectStorageResult(
    ObjectStorageStatus Status,
    ObjectReference? Reference = null,
    string? FailureCode = null);

/// <summary>
/// Stores original files in private object storage. Callers keep the opaque reference separate from
/// authorization and case ownership, which are resolved by authoritative application records.
/// </summary>
public interface IObjectStorage
{
    ValueTask<ObjectStorageResult> StoreAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    ValueTask<ObjectStorageResult> CopyToAsync(
        ObjectReference reference,
        Stream destination,
        CancellationToken cancellationToken);
}
