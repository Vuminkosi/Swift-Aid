using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>
/// Validates guest media bindings against server-issued upload reservations. Every attachment
/// must be reserved for the exact submitting (capability, session, client submission) triple,
/// still <c>Uploaded</c> (or already <c>Attached</c> by this same triple's committed report),
/// unexpired while awaiting first use, and within the caller-supplied size/duration bounds.
/// Binding mismatches (unknown, foreign, consumed-by-another-submission, or expired
/// reservations, duplicate references) report <c>InvalidBinding</c>; bound reservations
/// over quota report <c>TooLarge</c>.
/// </summary>
/// <remarks>
/// Validate-only: lifecycle transitions (<c>Uploaded</c> to <c>Attached</c>, expiry sweeps)
/// belong to the upload-lifecycle slice that also issues reservations. The reservation's
/// <c>ClientSubmissionId</c> scope already prevents one upload from being bound to two
/// different submissions.
/// </remarks>
public sealed class PostgresIntakeAttachmentAuthority : IIntakeAttachmentAuthority
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public PostgresIntakeAttachmentAuthority(SwiftAidDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IntakeAttachmentValidationStatus> ValidateSubmissionAttachmentsAsync(
        IntakeCapability capability,
        Guid clientSubmissionId,
        IReadOnlyList<Guid> attachmentIds,
        int maximumDurationSeconds,
        long maximumBytesPerAttachment,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(attachmentIds);
        if (capability.CapabilityId == Guid.Empty || capability.SessionId == Guid.Empty ||
            clientSubmissionId == Guid.Empty || maximumDurationSeconds < 1 || maximumBytesPerAttachment < 1)
        {
            return IntakeAttachmentValidationStatus.InvalidBinding;
        }

        if (attachmentIds.Count == 0)
        {
            return IntakeAttachmentValidationStatus.Valid;
        }

        if (attachmentIds.Any(id => id == Guid.Empty) ||
            attachmentIds.Distinct().Count() != attachmentIds.Count)
        {
            return IntakeAttachmentValidationStatus.InvalidBinding;
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var rows = await _dbContext.Set<IntakeAttachmentEntity>().AsNoTracking()
            .Where(item => attachmentIds.Contains(item.AttachmentId))
            .Select(item => new
            {
                item.AttachmentId,
                item.SessionId,
                item.CapabilityId,
                item.ClientSubmissionId,
                item.State,
                item.SizeBytes,
                item.DurationSeconds,
                item.ExpiresAtUtc
            })
            .ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length != attachmentIds.Count)
        {
            return IntakeAttachmentValidationStatus.InvalidBinding;
        }

        foreach (var row in rows)
        {
            if (row.SessionId != capability.SessionId ||
                row.CapabilityId != capability.CapabilityId ||
                row.ClientSubmissionId != clientSubmissionId)
            {
                return IntakeAttachmentValidationStatus.InvalidBinding;
            }

            // An Attached reservation bound to this exact triple is the idempotent
            // replay of the submission that consumed it; the commit path re-asserts
            // the binding without moving the row and returns the original receipt.
            if (row.State == IntakeAttachmentState.Attached)
            {
                continue;
            }

            if (row.State != IntakeAttachmentState.Uploaded ||
                row.ExpiresAtUtc <= now)
            {
                return IntakeAttachmentValidationStatus.InvalidBinding;
            }

            if (row.SizeBytes < 1 || row.SizeBytes > maximumBytesPerAttachment ||
                row.DurationSeconds < 1 || row.DurationSeconds > maximumDurationSeconds)
            {
                return IntakeAttachmentValidationStatus.TooLarge;
            }
        }

        return IntakeAttachmentValidationStatus.Valid;
    }
}
