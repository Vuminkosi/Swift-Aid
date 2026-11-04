using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Lists the calling officer's owned managed cases newest-first by the most
/// recently recorded journal event. Cases without journal rows fall back to the
/// projection timestamp. No sealed payloads are read.
/// </summary>
public sealed class PostgresCaseQueueStore(SwiftAidDbContext dbContext) : ICaseQueueStore, ICaseMapCandidateStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(string ownerActorId, int take, CancellationToken ct) =>
        ReadAsync(ownerActorId, take, null, null, null, ct, null, false, null, null, null, null);

    public Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(
        string ownerActorId, int take, CaseQueueFilter filter, CancellationToken ct) =>
        ReadAsync(ownerActorId, take, null, filter.CountryCode, null, ct, filter.Workflow, filter.NewestReportedFirst,
            filter.CaseIds, filter.ReportedFromUtc, filter.ReportedBeforeUtc, null);

    public Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAfterAsync(
        string ownerActorId, int take, CaseQueueFilter? filter, CaseQueueContinuation? after, CancellationToken ct) =>
        ReadAsync(ownerActorId, take, null, filter?.CountryCode, null, ct, filter?.Workflow,
            filter?.NewestReportedFirst ?? false, filter?.CaseIds, filter?.ReportedFromUtc,
            filter?.ReportedBeforeUtc, after);

    public Task<IReadOnlyList<CaseQueueCandidate>> GetAsync(string ownerActorId, string workspaceId,
        string countryCode, DateTimeOffset sinceUtc, int take, CancellationToken ct) =>
        ReadAsync(ownerActorId, take, workspaceId, countryCode, sinceUtc, ct, null, false, null, null, null, null);

    private async Task<IReadOnlyList<CaseQueueCandidate>> ReadAsync(
        string ownerActorId, int take, string? workspaceId, string? countryCode, DateTimeOffset? sinceUtc,
        CancellationToken ct, string? workflow, bool newestReportedFirst, IReadOnlyList<Guid>? caseIds,
        DateTimeOffset? reportedFromUtc, DateTimeOffset? reportedBeforeUtc, CaseQueueContinuation? after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "The queue take must be positive.");
        }

        var workflowValue = workflow is null ? (SwiftAid.Domain.Cases.CaseWorkflow?)null
            : Enum.Parse<SwiftAid.Domain.Cases.CaseWorkflow>(workflow);
        var query = _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.AccountableOwnerActorId == ownerActorId)
            .Where(item => caseIds == null || caseIds.Contains(item.CaseId))
            .Where(item => workspaceId == null || item.WorkspaceId == workspaceId)
            .Where(item => countryCode == null || item.CountryCode == countryCode)
            .Where(item => workflowValue == null || item.Workflow == workflowValue)
            .Where(item => sinceUtc == null || item.CreatedAtUtc >= sinceUtc)
            .Where(item => reportedFromUtc == null || item.CreatedAtUtc >= reportedFromUtc)
            .Where(item => reportedBeforeUtc == null || item.CreatedAtUtc < reportedBeforeUtc)
            .Select(item => new
            {
                item.CaseId,
                Workflow = item.Workflow.ToString(),
                item.Sector,
                item.Classification,
                item.WorkspaceId,
                item.CreatedAtUtc,
                item.CountryCode,
                item.AccountableOwnerActorId,
                item.AggregateVersion,
                item.UpdatedAtUtc,
                LastRecordedAtUtc = _dbContext.CaseEvents
                    .Where(journal => journal.CaseId == item.CaseId)
                    .Max(journal => (DateTimeOffset?)journal.RecordedAtUtc)
            });
        if (after is not null)
        {
            query = query.Where(item => (newestReportedFirst ? item.CreatedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
                    < after.SortTimestampUtc ||
                (newestReportedFirst ? item.CreatedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
                    == after.SortTimestampUtc &&
                item.CaseId.CompareTo(after.CaseId) > 0);
        }

        var rows = await query
            .OrderByDescending(item => newestReportedFirst ? item.CreatedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
            .ThenBy(item => item.CaseId)
            .Take(take)
            .ToArrayAsync(ct).ConfigureAwait(false);

        return rows
            .Select(item => new CaseQueueCandidate(
                item.CaseId,
                item.Workflow,
                item.CountryCode,
                item.AccountableOwnerActorId,
                item.AggregateVersion,
                item.UpdatedAtUtc,
                item.LastRecordedAtUtc ?? item.UpdatedAtUtc,
                item.Sector.ToString(),
                item.Classification.ToString(), item.WorkspaceId, item.CreatedAtUtc))
            .ToArray();
    }
}
