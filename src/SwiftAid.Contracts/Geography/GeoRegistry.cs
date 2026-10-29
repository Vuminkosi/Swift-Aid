namespace SwiftAid.Contracts.Geography;

/// <summary>One administrative ancestor, root first (country before region).</summary>
public sealed record GeoAdminAncestor(string Id, string Name, GeoLocationKind Kind);

/// <summary>Registry classification for a geographic record. Mirrors the curated seed vocabulary.</summary>
public enum GeoLocationKind
{
    Unknown = 0,
    Country = 1,
    Region = 2,
    Province = 3,
    Settlement = 4
}

/// <summary>Single registry hit for search-first location selection.</summary>
public sealed record GeoSearchResult(
    string Id,
    string Name,
    GeoLocationKind Kind,
    string CountryCode,
    string CountryName,
    string Ancestry,
    double? Latitude,
    double? Longitude,
    bool HasChildren,
    IReadOnlyList<GeoAdminAncestor> Ancestors);

/// <summary>Bounded search response; never the whole registry.</summary>
public sealed record GeoSearchResponse(
    string RegistryVersion,
    int TotalCount,
    IReadOnlyList<GeoSearchResult> Results);

/// <summary>One cascade level: the children of a registry parent.</summary>
public sealed record GeoChildrenResponse(
    string RegistryVersion,
    string ParentId,
    IReadOnlyList<GeoSearchResult> Children);
