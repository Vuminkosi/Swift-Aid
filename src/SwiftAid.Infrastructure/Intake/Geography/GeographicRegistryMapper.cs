using SwiftAid.Contracts.Geography;

namespace SwiftAid.Infrastructure.Intake.Geography;

internal static class GeographicRegistryMapper
{
    public static bool TryMapKind(string? locationType, out GeoLocationKind kind)
    {
        kind = locationType switch
        {
            "country" => GeoLocationKind.Country,
            "region" => GeoLocationKind.Region,
            "province" => GeoLocationKind.Province,
            "settlement_unclassified" => GeoLocationKind.Settlement,
            _ => GeoLocationKind.Unknown
        };
        return kind != GeoLocationKind.Unknown;
    }

    public static bool TryParseKind(string? locationKind, out GeoLocationKind kind)
    {
        kind = locationKind switch
        {
            nameof(GeoLocationKind.Country) => GeoLocationKind.Country,
            nameof(GeoLocationKind.Region) => GeoLocationKind.Region,
            nameof(GeoLocationKind.Province) => GeoLocationKind.Province,
            nameof(GeoLocationKind.Settlement) => GeoLocationKind.Settlement,
            _ => GeoLocationKind.Unknown
        };
        return kind != GeoLocationKind.Unknown;
    }
}
