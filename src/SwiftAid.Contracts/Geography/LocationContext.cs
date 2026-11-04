namespace SwiftAid.Contracts.Geography;

public enum LocationSource
{
    Reporter,
    Device,
    Geocoder
}

public enum LocationPrecision
{
    Exact,
    Approximate,
    Unknown
}

public sealed record OptionalPlace(string? ReportedName, string? NormalizedId, PresenceState Presence);

/// <summary>Occurrence location, distinct from reporter GPS, service coverage and jurisdiction.</summary>
public sealed record LocationContext(
    string CountryCode,
    OptionalPlace Region,
    OptionalPlace Area,
    OptionalPlace Locality,
    double? Latitude,
    double? Longitude,
    double? AccuracyMetres,
    LocationSource Source,
    LocationPrecision Precision,
    string? CatalogueVersion);

public enum PresenceState
{
    Known,
    Unknown,
    NotRecorded,
    NotApplicable,
    Absent
}
