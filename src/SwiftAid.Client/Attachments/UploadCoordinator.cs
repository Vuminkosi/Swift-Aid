using SwiftAid.Contracts.Attachments;

namespace SwiftAid.Client.Attachments;

/// <summary>
/// Coordinates resume of an original-media upload. The authoritative transfer offset must be
/// retrieved from the server; local state is not proof that a chunk was accepted.
/// </summary>
public interface IUploadCoordinator
{
    /// <summary>
    /// Refreshes persisted server status and resumes from its authoritative offset. Implementations
    /// must not expose a caller-selected case/comment/intake binding or treat an attachment ID as
    /// an authorization credential.
    /// </summary>
    ValueTask<AttachmentUploadStatus> ResumeAsync(
        AttachmentId attachmentId,
        CancellationToken cancellationToken);
}
