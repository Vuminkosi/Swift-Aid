using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Reads the managed-case monitoring projections. Open follow-ups are those without a
/// terminal outcome (Completed or Cancelled); missed and unreachable follow-ups stay
/// open until rescheduled. No sealed payloads are read.
/// </summary>
public sealed class PostgresCaseMonitoringSummaryStore(SwiftAidDbContext dbContext) : ICaseMonitoringSummaryStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

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

    public async Task<IReadOnlyList<CaseMonitoringEscalationItem>> GetEscalationsAsync(Guid caseId, CancellationToken ct)
    {
        var rows = await _dbContext.ManagedCaseEscalations.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderBy(item => item.RaisedVersion)
            .ThenBy(item => item.EscalationId)
            .Select(item => new CaseMonitoringEscalationItem(
                item.EscalationId,
                item.Status.ToString()))
            .ToArrayAsync(ct).ConfigureAwait(false);
        return rows;
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
