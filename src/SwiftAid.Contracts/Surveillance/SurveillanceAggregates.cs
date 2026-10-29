namespace SwiftAid.Contracts.Surveillance;

/// <summary>Access outcome for a scoped surveillance aggregate read.</summary>
public enum SurveillanceAggregateAccess
{
    Allowed,
    Denied,
    Unavailable
}

/// <summary>Geographic precision of a privacy-safe aggregate row.</summary>
public enum SurveillanceAggregatePrecision
{
    Country,
    Region
}

/// <summary>
/// A privacy-safe aggregate row over managed cases the caller may read.
/// Carries no coordinates, observation identifiers, or case identifiers.
/// Suppressed rows carry no counts or denominator.
/// </summary>
public sealed record SurveillanceAggregateArea(
    string AreaCode,
    string AreaLabel,
    SurveillanceAggregatePrecision Precision,
    int? HumanCount,
    int? AnimalCount,
    int? Denominator,
    bool IsSuppressed,
    string? SuppressionReason,
    DateTimeOffset AsOfUtc)
{
    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AreaCode) || AreaCode.Length is < 2 or > 64 ||
                string.IsNullOrWhiteSpace(AreaLabel) || AreaLabel.Length > 100 ||
                !Enum.IsDefined(Precision) || AsOfUtc == default ||
                HumanCount is < 0 || AnimalCount is < 0 || Denominator is < 0)
            {
                return false;
            }

            if (IsSuppressed)
            {
                return HumanCount is null && AnimalCount is null && Denominator is null &&
                    !string.IsNullOrWhiteSpace(SuppressionReason);
            }

            var knownCountTotal = (long)(HumanCount ?? 0) + (AnimalCount ?? 0);
            return (HumanCount.HasValue || AnimalCount.HasValue) &&
                string.IsNullOrWhiteSpace(SuppressionReason) &&
                (!Denominator.HasValue || knownCountTotal <= Denominator.Value);
        }
    }
}

/// <summary>
/// Authorized aggregate read outcome. An empty area list is a normal allowed
/// result: it never reveals whether other cases exist or who may read them.
/// </summary>
public sealed record SurveillanceAggregateResult(
    SurveillanceAggregateAccess Access,
    IReadOnlyList<SurveillanceAggregateArea> Areas,
    DateTimeOffset AsOfUtc,
    bool Truncated)
{
    public bool IsValid => Areas is not null && AsOfUtc != default && Areas.All(area => area.IsValid);
}
