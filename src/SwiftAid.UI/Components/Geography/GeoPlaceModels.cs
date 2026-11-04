namespace SwiftAid.UI.Components.Geography;

/// <summary>One administrative ancestor, root first. Carries the stable registry ID.</summary>
public sealed record GeoPlaceAncestor(string Id, string Name, string KindLabel);

/// <summary>Registry place as shown by the location picker. Carries display ancestry, never raw coordinates.
/// KindLabel uses the registry kind name ("Country", "Region", "Province", "Settlement").</summary>
public sealed record GeoPlaceOption(
    string Id,
    string Name,
    string KindLabel,
    string CountryCode,
    string CountryName,
    string Ancestry,
    bool HasChildren,
    IReadOnlyList<GeoPlaceAncestor> Ancestors);

public sealed record GeoPlaceCountry(string Code, string Name);

/// <summary>Outcome of one picker search or cascade load. Failures carry user-facing copy only.</summary>
public sealed record GeoPlaceQueryResult(
    bool Succeeded,
    IReadOnlyList<GeoPlaceOption> Options,
    string? ErrorMessage);

/// <summary>Registry coverage for the place picker. Mirrors the curated seed (Southern Africa).
/// Pages intersect this with the global country catalogue so out-of-coverage countries keep manual entry.</summary>
public static class GeoRegistryCoverage
{
    public static readonly IReadOnlyList<string> CountryCodes = ["SZ", "ZW", "ZA"];
}
