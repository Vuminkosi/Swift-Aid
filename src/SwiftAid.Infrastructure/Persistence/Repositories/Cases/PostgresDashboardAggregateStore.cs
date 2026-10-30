using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Reads officer-owned managed cases for dashboard aggregation. No sealed
/// payloads, narratives, or coordinates are read: only routing identity,
/// sector, classification, workflow, region links, and record times.
/// </summary>
public sealed class PostgresDashboardAggregateStore(SwiftAidDbContext dbContext) : IDashboardAggregateStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<DashboardAggregateCaseRow>> GetOwnedCaseRowsAsync(
        string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "The aggregate take must be positive.");
        }

        var rows = await _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.AccountableOwnerActorId == ownerActorId && item.CreatedAtUtc >= sinceUtc)
            .OrderBy(item => item.CaseId)
            .Select(item => new
            {
                item.CaseId,
                item.Sector,
                item.Classification,
                item.Workflow,
                item.CountryCode,
                item.CreatedAtUtc,
                item.UpdatedAtUtc
            })
            .Take(take)
            .ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length == 0)
        {
            return Array.Empty<DashboardAggregateCaseRow>();
        }

        var caseIds = rows.Select(item => item.CaseId).ToArray();
        var regionRows = await _dbContext.ManagedCaseRegions.AsNoTracking()
            .Where(region => caseIds.Contains(region.CaseId))
            .Select(region => new { region.CaseId, region.RegionId })
            .ToArrayAsync(ct).ConfigureAwait(false);
        var regionsByCase = regionRows
            .GroupBy(region => region.CaseId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(region => region.RegionId)
                    .Where(regionId => !string.IsNullOrWhiteSpace(regionId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());

        return rows
            .Select(item => new DashboardAggregateCaseRow(
                item.CaseId,
                item.Sector,
                item.Classification,
                item.Workflow,
                item.CountryCode,
                regionsByCase.TryGetValue(item.CaseId, out var regionIds)
                    ? regionIds
                    : Array.Empty<string>(),
                item.CreatedAtUtc,
                item.UpdatedAtUtc))
            .ToArray();
    }
}
