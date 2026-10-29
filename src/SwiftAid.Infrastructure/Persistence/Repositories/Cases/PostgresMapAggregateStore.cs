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
            .OrderBy(item => item.CaseId)
            .Select(item => new
            {
                item.CaseId,
                item.CountryCode,
                item.Sector,
                item.CreatedAtUtc,
                RegionIds = _dbContext.ManagedCaseRegions.AsNoTracking()
                    .Where(region => region.CaseId == item.CaseId)
                    .Select(region => region.RegionId)
                    .ToArray()
            })
            .Take(take)
            .ToArrayAsync(ct).ConfigureAwait(false);

        return rows
            .Select(item => new MapAggregateCaseRow(
                item.CaseId,
                item.CountryCode,
                item.RegionIds
                    .Where(regionId => !string.IsNullOrWhiteSpace(regionId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                item.Sector,
                item.CreatedAtUtc))
            .ToArray();
    }
}
