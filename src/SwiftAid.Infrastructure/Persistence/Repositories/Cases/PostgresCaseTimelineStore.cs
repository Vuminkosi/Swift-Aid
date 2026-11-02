using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Reads the managed-case projection and its authoritative journal rows. Never decrypts
/// sealed payloads; the timeline carries metadata only.
/// </summary>
public sealed class PostgresCaseTimelineStore(SwiftAidDbContext dbContext) : ICaseTimelineStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<CaseTimelineHeader?> GetHeaderAsync(Guid caseId, CancellationToken ct)
    {
        var header = await _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .Select(item => new CaseTimelineHeader(
                item.CaseId,
                item.OrganizationId,
                item.WorkspaceId,
                item.CountryCode,
                item.Workflow.ToString(),
                item.AccountableOwnerActorId,
                item.AggregateVersion,
                item.ReportRevision,
                item.CreatedAtUtc))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        // Read independently of the requested history page. A reopened case establishes
        // a new assignment even when the accountable owner has not changed.
        var assignmentVersion = await _dbContext.CaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId && item.AggregateVersion <= header.AggregateVersion &&
                (item.EventType == "CaseAssigned" || item.EventType == "CaseReopened"))
            .OrderByDescending(item => item.AggregateVersion)
            .Select(item => (long?)item.AggregateVersion)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (assignmentVersion is null)
        {
            return header;
        }

        var acknowledgedAt = await _dbContext.CaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId && item.EventType == "CaseAssignmentAcknowledged" &&
                item.AggregateVersion > assignmentVersion.Value && item.AggregateVersion <= header.AggregateVersion &&
                item.ActorId == header.AccountableOwnerActorId)
            .OrderByDescending(item => item.AggregateVersion)
            .Select(item => (DateTimeOffset?)item.RecordedAtUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return header with { AssignmentVersion = assignmentVersion, AssignmentAcknowledgedAtUtc = acknowledgedAt };
    }

    public async Task<IReadOnlyList<CaseTimelineRow>> GetEventsAsync(
        Guid caseId,
        int limit,
        long? beforeAggregateVersion,
        CancellationToken ct)
    {
        var events = _dbContext.CaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId);
        if (beforeAggregateVersion is { } cursor)
        {
            events = events.Where(item => item.AggregateVersion < cursor);
        }

        var rows = await events
            .OrderByDescending(item => item.AggregateVersion)
            .ThenByDescending(item => item.RecordedAtUtc)
            .Take(limit)
            .Select(item => new CaseTimelineRow(
                item.EventId,
                item.EventType,
                item.AggregateVersion,
                item.ReportRevision,
                item.CommandId,
                item.ActorId,
                item.OccurredAtUtc,
                item.RecordedAtUtc,
                item.SchemaVersion))
            .ToArrayAsync(ct).ConfigureAwait(false);
        return rows;
    }

    public Task<Guid?> GetPromotingReceiptAsync(Guid caseId, CancellationToken ct) =>
        _dbContext.IntakePromotions.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .Select(item => (Guid?)item.ReceiptId)
            .SingleOrDefaultAsync(ct);
}
