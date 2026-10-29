namespace SwiftAid.UI.Components.Geography;

/// <summary>Registry place as shown by the location picker. Carries display ancestry, never raw coordinates.</summary>
public sealed record GeoPlaceOption(
    string Id,
    string Name,
    string KindLabel,
    string CountryCode,
    string CountryName,
    string Ancestry,
    bool HasChildren);

public sealed record GeoPlaceCountry(string Code, string Name);

/// <summary>Outcome of one picker search or cascade load. Failures carry user-facing copy only.</summary>
public sealed record GeoPlaceQueryResult(
    bool Succeeded,
    IReadOnlyList<GeoPlaceOption> Options,
    string? ErrorMessage);
