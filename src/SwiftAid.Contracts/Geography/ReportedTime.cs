namespace SwiftAid.Contracts.Geography;

public enum ReportedTimePrecision
{
    Exact,
    DateOnly,
    Approximate,
    Unknown
}

/// <summary>Reporter-supplied occurrence time, kept distinct from capture, receipt and upload times.</summary>
public sealed record ReportedTime(
    DateTimeOffset? Value,
    DateOnly? LocalDate,
    DateOnly? RangeStart,
    DateOnly? RangeEnd,
    string? OriginalOffset,
    string? ZoneId,
    ReportedTimePrecision Precision);
