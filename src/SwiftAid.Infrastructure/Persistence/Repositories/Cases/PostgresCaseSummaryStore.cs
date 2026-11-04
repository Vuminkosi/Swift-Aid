using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Reads one managed case summary row with region links and owner display
/// name. No sealed payloads, narratives, observations, or coordinates are read.
/// </summary>
public sealed class PostgresCaseSummaryStore(SwiftAidDbContext dbContext) : ICaseSummaryStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<CaseSummaryData?> GetAsync(Guid caseId, CancellationToken ct)
    {
        if (caseId == Guid.Empty)
        {
            return null;
        }

        var row = await _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .Select(item => new
            {
                item.CaseId,
                item.Kind,
                item.Sector,
                item.Classification,
                item.Laboratory,
                item.OutcomeState,
                item.Workflow,
                item.CountryCode,
                item.AccountableOwnerActorId,
                item.CreatedAtUtc,
                item.UpdatedAtUtc
            })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var regions = await _dbContext.ManagedCaseRegions.AsNoTracking()
            .Where(region => region.CaseId == caseId && region.RegionId != null)
            .Select(region => region.RegionId!)
            .Distinct()
            .ToArrayAsync(ct).ConfigureAwait(false);
        var displayName = await _dbContext.UserProfiles.AsNoTracking()
            .Where(profile => profile.UserId == row.AccountableOwnerActorId)
            .Select(profile => profile.DisplayName)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);

        return new CaseSummaryData(
            row.CaseId,
            row.Kind,
            row.Sector,
            row.Classification,
            row.Workflow,
            row.CountryCode,
            regions,
            row.AccountableOwnerActorId,
            displayName,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.Laboratory,
            row.OutcomeState);
    }
}
