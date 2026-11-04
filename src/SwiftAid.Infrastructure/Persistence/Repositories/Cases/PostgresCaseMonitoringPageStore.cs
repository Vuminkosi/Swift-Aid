using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Reads a stable, bounded slice of open work belonging to an authorized case.</summary>
public sealed class PostgresCaseMonitoringPageStore(
    SwiftAidDbContext dbContext,
    ICaseMonitoringQueueStore queueStore) : ICaseMonitoringPageStore
{
    public async Task<IReadOnlyList<MonitoringQueueItem>> ReadAsync(
        ICaseCommandLease lease, int offset, int take, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsActive || lease.Mode != CaseLeaseMode.Read || lease.CaseId == Guid.Empty)
            throw new UnauthorizedAccessException("An active case read lease is required.");
        if (offset < 0 || take is < 1 or > 101)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var caseId = lease.CaseId;
        var followUps = dbContext.ManagedCaseFollowUps.AsNoTracking().Where(item => item.CaseId == caseId &&
            (item.Status == FollowUpStatus.Scheduled || item.Status == FollowUpStatus.Acknowledged));
        var escalations = dbContext.ManagedCaseEscalations.AsNoTracking().Where(item => item.CaseId == caseId &&
            item.Status != EscalationStatus.Closed);
        var assistance = dbContext.ManagedCaseAssistance.AsNoTracking().Where(item => item.CaseId == caseId &&
            item.Status != AssistanceStatus.ReceiptConfirmed && item.Status != AssistanceStatus.Cancelled);

        var followUpCount = await followUps.CountAsync(cancellationToken).ConfigureAwait(false);
        var escalationCount = await escalations.CountAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<MonitoringQueueItem>(take);
        if (offset < followUpCount)
        {
            var rows = await followUps.OrderBy(item => item.FollowUpId).Skip(offset).Take(take)
                .Select(item => new { item.FollowUpId, item.Purpose, item.AssigneeActorId, item.DueAtUtc,
                    item.Status, item.ScheduledVersion,
                    Recorded = dbContext.CaseEvents.Where(e => e.CaseId == caseId && e.AggregateVersion == item.ScheduledVersion)
                        .Select(e => (DateTimeOffset?)e.RecordedAtUtc).SingleOrDefault() })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            result.AddRange(rows.Select(item => new MonitoringQueueItem(caseId, MonitoringQueueKind.FollowUp,
                item.FollowUpId, item.Purpose, item.AssigneeActorId, item.DueAtUtc, item.Status.ToString(),
                item.DueAtUtc < utcNow, item.Recorded ?? item.DueAtUtc, item.ScheduledVersion)));
        }

        var escalationOffset = Math.Max(0, offset - followUpCount);
        if (result.Count < take && escalationOffset < escalationCount)
        {
            var rows = await escalations.OrderBy(item => item.EscalationId).Skip(escalationOffset).Take(take - result.Count)
                .Select(item => new { item.EscalationId, item.Status, item.RaisedVersion,
                    Recorded = dbContext.CaseEvents.Where(e => e.CaseId == caseId && e.AggregateVersion == item.RaisedVersion)
                        .Select(e => (DateTimeOffset?)e.RecordedAtUtc).SingleOrDefault() })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in rows)
            {
                var candidate = new MonitoringQueueCandidate(caseId, nameof(MonitoringQueueKind.Escalation), item.EscalationId,
                    item.Status switch { EscalationStatus.Raised => "Escalation raised", EscalationStatus.Acknowledged => "Escalation acknowledged",
                        EscalationStatus.ActionRecorded => "Escalation action recorded", _ => "Escalation" },
                    null, null, item.Status.ToString(), item.Recorded ?? utcNow, item.RaisedVersion);
                if (item.Status == EscalationStatus.Acknowledged)
                    candidate = await queueStore.ResolveEscalationStatusAsync(lease, candidate, cancellationToken).ConfigureAwait(false);
                result.Add(new MonitoringQueueItem(caseId, MonitoringQueueKind.Escalation, candidate.WorkItemId,
                    candidate.Summary, null, null, candidate.Status, false, candidate.RecordedAtUtc, candidate.AggregateVersion));
            }
        }

        var assistanceOffset = Math.Max(0, offset - followUpCount - escalationCount);
        if (result.Count < take)
        {
            var rows = await assistance.OrderBy(item => item.AssistanceId).Skip(assistanceOffset).Take(take - result.Count)
                .Select(item => new { item.AssistanceId, item.Need, item.LastNote, item.ResponsibleActorId, item.DueAtUtc,
                    item.Status, item.LastEventVersion,
                    Recorded = dbContext.CaseEvents.Where(e => e.CaseId == caseId && e.AggregateVersion == item.LastEventVersion)
                        .Select(e => (DateTimeOffset?)e.RecordedAtUtc).SingleOrDefault() })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in rows)
            {
                if (item.Recorded is null)
                    throw new InvalidDataException("An assistance projection has no matching event.");
                result.Add(new MonitoringQueueItem(caseId, MonitoringQueueKind.Assistance, item.AssistanceId,
                    item.Status == AssistanceStatus.Unfulfilled ? $"{item.Need} - Unfulfilled: {item.LastNote}" : item.Need,
                    item.ResponsibleActorId, item.DueAtUtc, item.Status.ToString(),
                    item.Status is AssistanceStatus.Requested or AssistanceStatus.Accepted or AssistanceStatus.InProgress &&
                        item.DueAtUtc.HasValue && item.DueAtUtc.Value < utcNow,
                    item.Recorded.Value, item.LastEventVersion));
            }
        }

        return result;
    }
}
