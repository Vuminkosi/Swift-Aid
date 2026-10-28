using SwiftAid.Client.Outbox;

namespace SwiftAid.Client.Cases;

/// <summary>UI-facing outcome for one queued case command. Never exposes payloads or keys.</summary>
public enum CaseCommandOutboxOutcome
{
    Pending,
    Accepted,
    Conflict,
    Denied,
    Invalid,
    Unknown,
}

/// <summary>One queued case command rendered for staff UI refresh and retry prompts.</summary>
public sealed record CaseCommandOutboxEntry(
    Guid CommandId,
    Guid CaseId,
    CaseCommandOutboxOutcome Outcome,
    LocalCommandState State,
    long? AcceptedAggregateVersion,
    DateTimeOffset? RetryAtUtc);

/// <summary>
/// Reads the retained outbox state behind <see cref="OfflineCaseCommandClient"/> so the
/// UI can mark Accepted/Conflict/Invalid/Denied outcomes and pending intents back to
/// staff. Fails closed: without a usable provisioned account it reports nothing.
/// </summary>
public interface ICaseCommandOutboxTracker
{
    Task<IReadOnlyList<CaseCommandOutboxEntry>> GetForCaseAsync(Guid caseId, CancellationToken cancellationToken);
    Task<CaseCommandOutboxEntry?> GetAsync(Guid commandId, CancellationToken cancellationToken);
}

public sealed class CaseCommandOutboxTracker(ICommandOutbox outbox) : ICaseCommandOutboxTracker
{
    public async Task<IReadOnlyList<CaseCommandOutboxEntry>> GetForCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        if (caseId == Guid.Empty)
        {
            return [];
        }

        try
        {
            var statuses = await outbox.GetStatusesAsync(cancellationToken).ConfigureAwait(false);
            return statuses
                .Where(item => item.CaseId == caseId)
                .Select(ToEntry)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async Task<CaseCommandOutboxEntry?> GetAsync(Guid commandId, CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var statuses = await outbox.GetStatusesAsync(cancellationToken).ConfigureAwait(false);
            var status = statuses.SingleOrDefault(item => item.CommandId == commandId);
            return status is null ? null : ToEntry(status);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CaseCommandOutboxEntry ToEntry(LocalCommandStatus status) => new(
        status.CommandId,
        status.CaseId,
        status.State switch
        {
            LocalCommandState.Queued => CaseCommandOutboxOutcome.Pending,
            LocalCommandState.Sending => CaseCommandOutboxOutcome.Pending,
            LocalCommandState.RetryScheduled => CaseCommandOutboxOutcome.Pending,
            LocalCommandState.Acknowledged => CaseCommandOutboxOutcome.Accepted,
            LocalCommandState.Synchronized => CaseCommandOutboxOutcome.Accepted,
            LocalCommandState.BlockedConflict => CaseCommandOutboxOutcome.Conflict,
            LocalCommandState.BlockedAuthorization => CaseCommandOutboxOutcome.Denied,
            LocalCommandState.RejectedValidation => CaseCommandOutboxOutcome.Invalid,
            _ => CaseCommandOutboxOutcome.Unknown,
        },
        status.State,
        status.AcceptedAggregateVersion,
        status.RetryAtUtc);
}
