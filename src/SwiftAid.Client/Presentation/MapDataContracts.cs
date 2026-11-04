namespace SwiftAid.Client.Presentation;

/// <summary>Describes the geographic precision of a privacy-safe aggregate area.</summary>
public enum MapAggregateAreaPrecision
{
    Country,
    Region
}

/// <summary>A coarse public sector layer; no health-pattern or diagnosis category is represented.</summary>
public enum MapAggregateSector
{
    Human,
    Animal
}

/// <summary>The fixed reporting window accepted by aggregate map queries.</summary>
public enum MapAggregatePeriod
{
    Days30 = 30,
    Days90 = 90
}

/// <summary>A bounded, non-identifying map query. It never accepts a coordinate or case identifier.</summary>
public sealed record MapAggregateQuery(
    PageRequest Page,
    string? RegionCode = null,
    MapAggregatePeriod Period = MapAggregatePeriod.Days30)
{
    public bool IsValid => Page.IsValid &&
        Enum.IsDefined(Period) &&
        (string.IsNullOrWhiteSpace(RegionCode) ||
         (RegionCode.Length is >= 2 and <= 24 && RegionCode.All(IsRegionCodeCharacter)));

    private static bool IsRegionCodeCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '-';
}

/// <summary>
/// A privacy-safe aggregate area. Suppressed rows contain no counts or denominator.
/// This view intentionally contains no coordinates, observation IDs, or case IDs.
/// </summary>
public sealed record MapAreaAggregate(
    string AreaCode,
    string AreaLabel,
    MapAggregateAreaPrecision Precision,
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
            if (string.IsNullOrWhiteSpace(AreaCode) || AreaCode.Length is < 2 or > 24 ||
                !AreaCode.All(IsRegionCodeCharacter) || string.IsNullOrWhiteSpace(AreaLabel) ||
                AreaLabel.Length > 100 || !Enum.IsDefined(Precision) || AsOfUtc == default ||
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

    private static bool IsRegionCodeCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '-';
}

public sealed record MapAggregatePage(
    Page<MapAreaAggregate> Areas,
    DateTimeOffset AsOfUtc)
{
    public bool IsValid => Areas is not null && Areas.Items is not null &&
        Areas.PageNumber > 0 && Areas.PageSize is > 0 and <= 100 &&
        Areas.TotalCount >= Areas.Items.Count && AsOfUtc != default &&
        Areas.Items.All(area => area.IsValid) &&
        Areas.Items.Select(area => area.AreaCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Areas.Items.Count;
}

/// <summary>Reads only coarse, access-filtered area aggregates suitable for a schematic map.</summary>
public interface IMapAggregateClient
{
    Task<UiCapabilityResult<MapAggregatePage>> GetAsync(
        MapAggregateQuery query,
        CancellationToken cancellationToken = default);
}
