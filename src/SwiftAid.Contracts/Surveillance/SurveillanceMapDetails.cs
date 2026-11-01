namespace SwiftAid.Contracts.Surveillance;

/// <summary>Suppression-checked report totals at a public registry place, never a patient coordinate.</summary>
public sealed record SurveillanceMapPoint(string Id, string Label, double Latitude, double Longitude,
    int HumanReports, int AnimalReports);

public sealed record SurveillanceMapSignal(Guid Id, string Label, string Area, string State,
    int Reports, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Comparison, string CaseHref);

public sealed record SurveillanceMapDetails(IReadOnlyList<SurveillanceMapPoint> Points,
    IReadOnlyList<SurveillanceMapSignal> Signals, bool Truncated, int UnmappedReports);
