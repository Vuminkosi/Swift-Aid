using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Reads officer-owned managed cases for area aggregation. No sealed
/// payloads, narratives, or coordinates are read: only routing identity,
/// sector, region links, and record creation time.
/// </summary>
public sealed class PostgresMapAggregateStore(SwiftAidDbContext dbContext) : IMapAggregateStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<MapAggregateCaseRow>> GetOwnedCaseAreasAsync(
        string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "The aggregate take must be positive.");
        }

        var rows = await _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.AccountableOwnerActorId == ownerActorId && item.CreatedAtUtc >= sinceUtc)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenBy(item => item.CaseId)
            .Select(item => new
            {
                item.CaseId,
                item.CountryCode,
                item.Sector,
                item.CreatedAtUtc
            })
            .Take(take)
            .ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length == 0)
        {
            return Array.Empty<MapAggregateCaseRow>();
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
            .Select(item => new MapAggregateCaseRow(
                item.CaseId,
                item.CountryCode,
                regionsByCase.TryGetValue(item.CaseId, out var regionIds)
                    ? regionIds
                    : Array.Empty<string>(),
                item.Sector,
                item.CreatedAtUtc))
            .ToArray();
    }
}
