using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Lists open monitoring work newest-relevant-first from the monitoring projections.
/// Open follow-ups are Scheduled (including rescheduled); open escalations are Raised.
/// No sealed payloads are read.
/// </summary>
public sealed class PostgresCaseMonitoringQueueStore(SwiftAidDbContext dbContext) : ICaseMonitoringQueueStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssignedFollowUpsAsync(
        string assigneeActorId, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assigneeActorId);
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "The queue take must be positive.");
        }

        var rows = await _dbContext.ManagedCaseFollowUps.AsNoTracking()
            .Where(item => item.AssigneeActorId == assigneeActorId && item.Status == FollowUpStatus.Scheduled)
            .OrderBy(item => item.DueAtUtc)
            .ThenBy(item => item.FollowUpId)
            .Take(take)
            .Select(item => new
            {
                item.CaseId,
                item.FollowUpId,
                item.Purpose,
                item.AssigneeActorId,
                item.DueAtUtc,
                item.Status,
                item.ScheduledVersion,
                RecordedAtUtc = _dbContext.CaseEvents
                    .Where(journal => journal.CaseId == item.CaseId && journal.AggregateVersion == item.ScheduledVersion)
                    .Select(journal => (DateTimeOffset?)journal.RecordedAtUtc)
                    .SingleOrDefault()
            })
            .ToArrayAsync(ct).ConfigureAwait(false);

        return rows
            .Select(item => new MonitoringQueueCandidate(
                item.CaseId,
                nameof(MonitoringQueueKind.FollowUp),
                item.FollowUpId,
                item.Purpose,
                item.AssigneeActorId,
                item.DueAtUtc,
                item.Status.ToString(),
                item.RecordedAtUtc ?? item.DueAtUtc,
                item.ScheduledVersion))
            .ToArray();
    }

    public async Task<IReadOnlyList<MonitoringQueueCandidate>> GetOpenFollowUpsAsync(
        int take, CancellationToken ct)
    {
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "The queue take must be positive.");
        }

        var rows = await _dbContext.ManagedCaseFollowUps.AsNoTracking()
            .Where(item => item.Status == FollowUpStatus.Scheduled)
            .OrderBy(item => item.DueAtUtc)
            .ThenBy(item => item.FollowUpId)
            .Take(take)
            .Select(item => new
            {
                item.CaseId,
                item.FollowUpId,
                item.Purpose,
                item.AssigneeActorId,
                item.DueAtUtc,
                item.Status,
                item.ScheduledVersion,
                RecordedAtUtc = _dbContext.CaseEvents
                    .Where(journal => journal.CaseId == item.CaseId && journal.AggregateVersion == item.ScheduledVersion)
                    .Select(journal => (DateTimeOffset?)journal.RecordedAtUtc)
                    .SingleOrDefault()
            })
            .ToArrayAsync(ct).ConfigureAwait(false);

        return rows
            .Select(item => new MonitoringQueueCandidate(
                item.CaseId,
                nameof(MonitoringQueueKind.FollowUp),
                item.FollowUpId,
                item.Purpose,
                item.AssigneeActorId,
                item.DueAtUtc,
                item.Status.ToString(),
                item.RecordedAtUtc ?? item.DueAtUtc,
                item.ScheduledVersion))
            .ToArray();
    }

    public async Task<IReadOnlyList<MonitoringQueueCandidate>> GetRaisedEscalationsAsync(
        int take, CancellationToken ct)
    {
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "The queue take must be positive.");
        }

        var rows = await _dbContext.ManagedCaseEscalations.AsNoTracking()
            .Where(item => item.Status == EscalationStatus.Raised)
            .OrderBy(item => item.RaisedVersion)
            .ThenBy(item => item.EscalationId)
            .Take(take)
            .Select(item => new
            {
                item.CaseId,
                item.EscalationId,
                item.Status,
                item.RaisedVersion,
                RecordedAtUtc = _dbContext.CaseEvents
                    .Where(journal => journal.CaseId == item.CaseId && journal.AggregateVersion == item.RaisedVersion)
                    .Select(journal => (DateTimeOffset?)journal.RecordedAtUtc)
                    .SingleOrDefault()
            })
            .ToArrayAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        return rows
            .Select(item => new MonitoringQueueCandidate(
                item.CaseId,
                nameof(MonitoringQueueKind.Escalation),
                item.EscalationId,
                "Escalation raised",
                null,
                null,
                item.Status.ToString(),
                item.RecordedAtUtc ?? now,
                item.RaisedVersion))
            .ToArray();
    }

    public async Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssignedAssistanceAsync(
        string actorId, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ValidateTake(take);
        return await GetAssistanceAsync(item => item.ResponsibleActorId == actorId &&
            (item.Status == AssistanceStatus.Requested || item.Status == AssistanceStatus.Accepted ||
             item.Status == AssistanceStatus.InProgress || item.Status == AssistanceStatus.Provided ||
             item.Status == AssistanceStatus.Unfulfilled), take, ct)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MonitoringQueueCandidate>> GetOpenAssistanceAsync(int take, CancellationToken ct)
    {
        ValidateTake(take);
        return GetAssistanceAsync(item => item.Status != AssistanceStatus.ReceiptConfirmed &&
            item.Status != AssistanceStatus.Cancelled, take, ct);
    }

    private async Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssistanceAsync(
        System.Linq.Expressions.Expression<Func<ManagedCaseAssistanceEntity, bool>> predicate,
        int take, CancellationToken ct)
    {
        var rows = await _dbContext.ManagedCaseAssistance.AsNoTracking()
            .Where(predicate)
            .OrderBy(item => item.DueAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.AssistanceId)
            .Take(take)
            .Select(item => new
            {
                item.CaseId, item.AssistanceId, item.Need, item.LastNote, item.ResponsibleActorId,
                item.DueAtUtc, item.Status, item.LastEventVersion,
                RecordedAtUtc = _dbContext.CaseEvents
                    .Where(journal => journal.CaseId == item.CaseId && journal.AggregateVersion == item.LastEventVersion)
                    .Select(journal => (DateTimeOffset?)journal.RecordedAtUtc)
                    .SingleOrDefault()
            })
            .ToArrayAsync(ct).ConfigureAwait(false);

        return rows.Where(item => item.RecordedAtUtc.HasValue).Select(item => new MonitoringQueueCandidate(
            item.CaseId, nameof(MonitoringQueueKind.Assistance), item.AssistanceId,
            item.Status == AssistanceStatus.Unfulfilled
                ? $"{item.Need} - Unfulfilled: {item.LastNote}"
                : item.Need,
            item.ResponsibleActorId, item.DueAtUtc, item.Status.ToString(),
            item.RecordedAtUtc!.Value, item.LastEventVersion)).ToArray();
    }

    private static void ValidateTake(int take)
    {
        if (take < 1) throw new ArgumentOutOfRangeException(nameof(take), "The queue take must be positive.");
    }
}
