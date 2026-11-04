using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Application.Cases.Spatial;

/// <summary>Ranks privacy-safe administrative counts from the existing per-case authorized read model.</summary>
public sealed class OwnedCaseSpatialAnalysis(IMapAggregateQuery map)
{
    public const int WindowDays = 30;
    public const string Method = "authorized-owned-case-count-by-reported-region/1";
    public const string Limitations = "Counts cover only readable cases owned by the requesting officer. " +
        "The time window uses managed-case creation time. These are not population prevalence, incidence, hotspots, or exact case locations. " +
        "Cases with unknown regions are excluded; a case reported in multiple regions may appear in more than one count.";

    public async Task<SpatialAnalysisResult> GetAsync(ActorContext actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var result = await map.GetAsync(actor, null, TimeSpan.FromDays(WindowDays), ct).ConfigureAwait(false);
        var end = result.AsOfUtc == default ? DateTimeOffset.UtcNow : result.AsOfUtc.ToUniversalTime();
        var start = end.AddDays(-WindowDays);
        if (result.Access != SurveillanceAggregateAccess.Allowed || result.Truncated || !result.IsValid)
            return new(SpatialAnalysisAccess.Unavailable, [], start, end, SurveillanceMapAggregateQuery.SuppressionThreshold, Method, Limitations);
        var regions = result.Areas.Where(area => area.Precision == SurveillanceAggregatePrecision.Region && !area.IsSuppressed &&
                area.Denominator >= SurveillanceMapAggregateQuery.SuppressionThreshold)
            .Select(area => new SpatialRegionCount(area.AreaCode, area.AreaLabel, area.Denominator!.Value))
            .OrderByDescending(area => area.CaseCount).ThenBy(area => area.RegionLabel, StringComparer.Ordinal)
            .ThenBy(area => area.RegionId, StringComparer.Ordinal).ToArray();
        return new(SpatialAnalysisAccess.Allowed, regions, start, end, SurveillanceMapAggregateQuery.SuppressionThreshold, Method, Limitations);
    }
}
