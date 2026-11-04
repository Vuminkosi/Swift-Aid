namespace SwiftAid.Contracts.Cases;

/// <summary>Original report evidence, returned only after current case-read authorization.</summary>
public sealed record CaseReportDetails(
    Guid CaseId,
    long ReportRevision,
    string? Narrative,
    IReadOnlyList<string> Observations,
    CaseReportPlace? Location,
    CaseNearbyFacilities? NearbyFacilities = null);

/// <summary>Distances computed privately from occurrence coordinates, never reporter GPS.</summary>
public sealed record CaseNearbyFacilities(
    NearbyMedicalFacility? Clinic,
    NearbyMedicalFacility? Hospital,
    bool ApproximateLocation,
    double? LocationAccuracyMetres,
    string? LocalityCentre = null);

public sealed record NearbyMedicalFacility(string Name, string MapUrl, double DistanceKilometres);

/// <summary>Reported place labels and registry references; never precise report coordinates.</summary>
public sealed record CaseReportPlace(
    string CountryCode,
    string? Region,
    string? Area,
    string? Locality,
    string? RegionId,
    string? AreaId,
    string? LocalityId);
