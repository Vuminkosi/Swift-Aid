using SwiftAid.Application.Abstractions;

namespace SwiftAid.Application.Attachments;

/// <summary>
/// Reads a bounded byte range from an opaque object stored in the private media bucket.
/// Authorization and attachment ownership must be resolved by the caller before using this port.
/// </summary>
public interface IPrivateMediaObjectStore
{
    /// <summary>
    /// Opens the requested inclusive range from an immutable private object. Returns <see langword="null"/>
    /// when the opaque reference does not exist. An out-of-bounds range is rejected.
    /// </summary>
    ValueTask<AttachmentRangeContent?> OpenReadAsync(
        ObjectReference reference,
        AttachmentByteRange range,
        CancellationToken cancellationToken);
}
