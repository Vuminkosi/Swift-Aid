using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Intake;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>Matches only explicitly configured active catchments; no broad or guessed fallback routing.</summary>
public sealed class PostgresIntakeCoverageCatalog(
    SwiftAidDbContext dbContext,
    IntakeMetadataOptions metadataOptions,
    TimeProvider timeProvider) : IIntakeCoverageCatalog
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IntakeMetadataOptions _metadataOptions = metadataOptions ?? throw new ArgumentNullException(nameof(metadataOptions));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IntakeCoverageDecision> FindAsync(
        LocationContext location,
        CaseKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!Enum.IsDefined(kind) || !string.Equals(location.CatalogueVersion, _metadataOptions.CatalogueVersion, StringComparison.Ordinal))
        {
            return new IntakeCoverageDecision(IntakeCoverageMatch.Ambiguous, null);
        }

        var now = _timeProvider.GetUtcNow();
        var matches = await _dbContext.Set<IntakeRouteEntity>().AsNoTracking()
            .Where(route => route.IsActive && route.CountryCode == location.CountryCode &&
                route.CatalogueVersion == location.CatalogueVersion && route.EffectiveFromUtc <= now &&
                (route.ExpiresAtUtc == null || route.ExpiresAtUtc > now) &&
                (route.RegionId == null || route.RegionId == location.Region.NormalizedId) &&
                (route.AreaId == null || route.AreaId == location.Area.NormalizedId))
            .Select(route => new
            {
                route.IntakeQueueId,
                Specificity = (route.RegionId == null ? 0 : 1) + (route.AreaId == null ? 0 : 1)
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return new IntakeCoverageDecision(IntakeCoverageMatch.None, null);
        }

        var highestSpecificity = matches.Max(match => match.Specificity);
        var selected = matches.Where(match => match.Specificity == highestSpecificity)
            .Select(match => match.IntakeQueueId).Distinct(StringComparer.Ordinal).ToArray();
        return selected.Length == 1
            ? new IntakeCoverageDecision(IntakeCoverageMatch.Eligible, selected[0])
            : new IntakeCoverageDecision(IntakeCoverageMatch.Ambiguous, null);
    }
}
