using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Protection;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Reads the managed-case monitoring projections. Open follow-ups are those without a
/// terminal outcome (Completed or Cancelled); missed and unreachable follow-ups stay
/// open until rescheduled. Legacy acknowledgement payloads are opened only when an
/// acknowledged escalation needs its action state derived from the authoritative journal.
/// </summary>
public sealed class PostgresCaseMonitoringSummaryStore(
    SwiftAidDbContext dbContext,
    IProtectedCaseEventPayloadStore payloadStore) : ICaseMonitoringSummaryStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IProtectedCaseEventPayloadStore _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));

    public async Task<IReadOnlyList<CaseMonitoringFollowUpItem>> GetOpenFollowUpsAsync(Guid caseId, CancellationToken ct)
    {
        var rows = await _dbContext.ManagedCaseFollowUps.AsNoTracking()
            .Where(item => item.CaseId == caseId &&
                item.Status != FollowUpStatus.Completed && item.Status != FollowUpStatus.Cancelled)
            .OrderBy(item => item.DueAtUtc)
            .ThenBy(item => item.FollowUpId)
            .Select(item => new CaseMonitoringFollowUpItem(
                item.FollowUpId,
                item.Purpose,
                item.AssigneeActorId,
                item.DueAtUtc,
                item.Status.ToString()))
            .ToArrayAsync(ct).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<CaseMonitoringEscalationItem>> GetEscalationsAsync(
        ICaseCommandLease lease, Guid caseId, CancellationToken ct)
    {
        if (lease.CaseId != caseId || !lease.IsActive || lease.Mode != CaseLeaseMode.Read)
        {
            throw new UnauthorizedAccessException("An active case read lease is required to read escalation status.");
        }

        var rows = await _dbContext.ManagedCaseEscalations.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderBy(item => item.RaisedVersion)
            .ThenBy(item => item.EscalationId)
            .Select(item => new CaseMonitoringEscalationItem(
                item.EscalationId,
                item.Status.ToString()))
            .ToArrayAsync(ct).ConfigureAwait(false);
        var legacyActionRecordedIds = rows.Any(item => item.Status == nameof(EscalationStatus.Acknowledged))
            ? (await LegacyEscalationActionReader.ReadAsync(_dbContext, _payloadStore, lease, ct).ConfigureAwait(false)).ActionRecordedIds
            : new HashSet<Guid>();
        return rows.Select(item => item.Status == nameof(EscalationStatus.Acknowledged) &&
                legacyActionRecordedIds.Contains(item.EscalationId)
            ? item with { Status = nameof(EscalationStatus.ActionRecorded) }
            : item).ToArray();
    }

    public Task<CaseMonitoringObservationItem?> GetLatestObservationAsync(Guid caseId, CancellationToken ct) =>
        _dbContext.ManagedCaseObservations.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderByDescending(item => item.AggregateVersion)
            .ThenByDescending(item => item.ObservationId)
            .Select(item => new CaseMonitoringObservationItem(
                item.ObservationId,
                item.Narrative,
                item.Source,
                item.AggregateVersion))
            .FirstOrDefaultAsync(ct);
}
