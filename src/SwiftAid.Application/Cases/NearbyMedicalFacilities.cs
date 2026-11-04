using System.Text.Json;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.Application.Cases;

/// <summary>Pure geographical lookup against the same public catalogue used by the map.</summary>
public static class NearbyMedicalFacilities
{
    public static async Task<CaseNearbyFacilities?> FindFromLocalityAsync(
        CaseReportPlace? place, IGeographicRegistry registry, CancellationToken ct)
    {
        var name = place?.Locality ?? place?.Area;
        if (place is null || string.IsNullOrWhiteSpace(name)) return null;
        var id = place.LocalityId ?? place.AreaId;
        var found = await registry.SearchAsync(name, place.CountryCode, 20, ct);
        if (found.TotalCount > 20) return null;
        var matches = found.Results.Where(item => item.Kind == GeoLocationKind.Settlement &&
            item.CountryCode.Equals(place.CountryCode, StringComparison.OrdinalIgnoreCase) &&
            (id is not null ? item.Id == id : item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
            (place.RegionId is not null ? item.Ancestors.Any(parent => parent.Id == place.RegionId) :
                place.Region is null || item.Ancestors.Any(parent => parent.Name.Equals(place.Region, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        if (matches is not [{ Latitude: { } latitude, Longitude: { } longitude } match]) return null;
        var unknown = new OptionalPlace(null, null, PresenceState.Unknown);
        var result = Find(new(place.CountryCode, unknown, unknown, unknown, latitude, longitude, null,
            LocationSource.Geocoder, LocationPrecision.Approximate, found.RegistryVersion));
        return result is null ? null : result with { LocalityCentre = match.Name };
    }

    private static readonly Lazy<Catalogue> Data = new(() =>
    {
        using var stream = typeof(NearbyMedicalFacilities).Assembly.GetManifestResourceStream("SwiftAid.MedicalCenters.json")
            ?? throw new InvalidOperationException("Facility catalogue is missing.");
        return JsonSerializer.Deserialize<Catalogue>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Facility catalogue is invalid.");
    });

    public static CaseNearbyFacilities? Find(LocationContext? location)
    {
        if (location is null || location.Precision == LocationPrecision.Unknown ||
            location.Latitude is not { } latitude || location.Longitude is not { } longitude ||
            !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
            return null;

        NearbyMedicalFacility? Nearest(string type) => Data.Value.Facilities
            .Where(item => item.Country.Equals(location.CountryCode, StringComparison.OrdinalIgnoreCase) && item.Type == type)
            .Select(item => new NearbyMedicalFacility(item.Name,
                "https://www.openstreetmap.org/" + item.Id,
                DistanceKilometres(latitude, longitude, item.Latitude, item.Longitude)))
            .OrderBy(item => item.DistanceKilometres).ThenBy(item => item.MapUrl, StringComparer.Ordinal)
            .FirstOrDefault();

        return new(Nearest("Clinic"), Nearest("Hospital"), location.Precision != LocationPrecision.Exact,
            location.AccuracyMetres is double accuracy && accuracy >= 0 && double.IsFinite(accuracy) ? accuracy : null);
    }

    public static double DistanceKilometres(double latitude, double longitude, double targetLatitude, double targetLongitude)
    {
        const double radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((targetLatitude - latitude) * radians / 2), 2) +
            Math.Cos(latitude * radians) * Math.Cos(targetLatitude * radians) *
            Math.Pow(Math.Sin((targetLongitude - longitude) * radians / 2), 2);
        return 6371.0088 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }

    private sealed record Catalogue(Facility[] Facilities);
    private sealed record Facility(string Id, string Country, string Name, string Type, double Latitude, double Longitude);
}
