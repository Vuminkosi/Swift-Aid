using SwiftAid.Application.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>
/// Fail-closed placeholder until the Postgres attachment-binding authority lands.
/// Rejects every attachment as unbound without state changes. Replace with an
/// <c>AddScoped</c> Postgres implementation; this <c>TryAdd</c> yields to it.
/// </summary>
public sealed class NotConfiguredIntakeAttachmentAuthority : IIntakeAttachmentAuthority
{
    public Task<IntakeAttachmentValidationStatus> ValidateSubmissionAttachmentsAsync(
        IntakeCapability capability,
        Guid clientSubmissionId,
        IReadOnlyList<Guid> attachmentIds,
        int maximumDurationSeconds,
        long maximumBytesPerAttachment,
        CancellationToken ct) =>
        Task.FromResult(IntakeAttachmentValidationStatus.InvalidBinding);
}
