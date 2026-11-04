namespace SwiftAid.Contracts.Surveillance;

/// <summary>Counts of readable, actor-owned case records by administrative region.</summary>
public sealed record SpatialRegionCount(string RegionId, string RegionLabel, int CaseCount);

public enum SpatialAnalysisAccess { Allowed, Unavailable }

/// <summary>Bounded administrative counts, not an incidence estimate or precise hotspot map.</summary>
public sealed record SpatialAnalysisResult(SpatialAnalysisAccess Access, IReadOnlyList<SpatialRegionCount> Regions,
    DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc, int MinimumCount, string Method, string Limitations);
