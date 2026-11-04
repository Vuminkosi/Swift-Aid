using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.Application.Tests;

public sealed class NearbyMedicalFacilitiesTests
{
    [Fact]
    public async Task LocalityEstimateRequiresUniqueSettlementInTheReportedCountryAndRegion()
    {
        var place = new CaseReportPlace("SZ", "Manzini", null, "Test locality", null, null, null);
        var hit = new GeoSearchResult("place", "Test locality", GeoLocationKind.Settlement, "SZ", "Eswatini",
            "Manzini", -26.5, 31.5, false, [new("region", "Manzini", GeoLocationKind.Region)]);
        var result = await NearbyMedicalFacilities.FindFromLocalityAsync(place, new Registry([hit]), default);
        Assert.Equal("Test locality", result!.LocalityCentre);
        Assert.True(result.ApproximateLocation);
        Assert.Null(result.LocationAccuracyMetres);
        Assert.NotNull(result.Clinic);
        Assert.NotNull(result.Hospital);
        Assert.Null(await NearbyMedicalFacilities.FindFromLocalityAsync(place, new Registry([hit, hit with { Id = "other" }]), default));
        Assert.Null(await NearbyMedicalFacilities.FindFromLocalityAsync(place, new Registry([hit with { CountryCode = "ZW" }]), default));
        Assert.Null(await NearbyMedicalFacilities.FindFromLocalityAsync(place, new Registry([hit with { Ancestors = [] }]), default));
        Assert.Null(await NearbyMedicalFacilities.FindFromLocalityAsync(place, new Registry([hit with { Kind = GeoLocationKind.Region }]), default));
        Assert.Null(await NearbyMedicalFacilities.FindFromLocalityAsync(place with { Locality = null }, new Registry([hit]), default));
    }

    private sealed class Registry(IReadOnlyList<GeoSearchResult> results) : IGeographicRegistry
    {
        public Task<GeoSearchResponse> SearchAsync(string query, string? countryCode, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new GeoSearchResponse("test", results.Count, results));
        public Task<GeoChildrenResponse> GetChildrenAsync(string parentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void HaversineUsesKilometresAndHandlesCoincidentAndAntipodalPoints()
    {
        Assert.Equal(0, NearbyMedicalFacilities.DistanceKilometres(-26, 31, -26, 31));
        Assert.InRange(NearbyMedicalFacilities.DistanceKilometres(0, 0, 0, 1), 111.19, 111.20);
        Assert.InRange(NearbyMedicalFacilities.DistanceKilometres(0, 0, 0, 180), 20015, 20016);
    }

    [Fact]
    public void ExactFacilityLocationsSelectTheMatchingTypeAtZeroDistance()
    {
        var clinic = NearbyMedicalFacilities.Find(Location("ZW") with
        {
            Latitude = -16.8846785, Longitude = 29.1968405, Precision = LocationPrecision.Exact
        })!;
        Assert.Equal("Nyamhunga Clinic", clinic.Clinic!.Name);
        Assert.Equal(0, clinic.Clinic.DistanceKilometres);
        Assert.True(clinic.Hospital!.DistanceKilometres > 0);
        Assert.False(clinic.ApproximateLocation);

        var hospital = NearbyMedicalFacilities.Find(Location("SZ") with
        {
            Latitude = -26.3314968, Longitude = 31.1437158
        })!;
        Assert.Equal("Mbabane Government Hospital", hospital.Hospital!.Name);
        Assert.Equal(0, hospital.Hospital.DistanceKilometres);
        Assert.True(hospital.Clinic!.DistanceKilometres > 0);
    }

    [Fact]
    public void UnknownInvalidAndMissingCoordinatesNeverInventDistances()
    {
        Assert.Null(NearbyMedicalFacilities.Find(null));
        Assert.Null(NearbyMedicalFacilities.Find(Location("ZW") with { Latitude = null }));
        Assert.Null(NearbyMedicalFacilities.Find(Location("ZW") with { Latitude = double.NaN }));
        Assert.Null(NearbyMedicalFacilities.Find(Location("ZW") with { Precision = LocationPrecision.Unknown }));
        Assert.Null(NearbyMedicalFacilities.Find(Location("ZW") with { Longitude = 181 }));
    }

    [Fact]
    public void BothTypesAreReturnedOnlyFromReportedCountryWithPrecisionAndPublicLinks()
    {
        var result = NearbyMedicalFacilities.Find(Location("ZW"))!;
        Assert.NotNull(result.Clinic);
        Assert.NotNull(result.Hospital);
        Assert.True(result.ApproximateLocation);
        Assert.Equal(500, result.LocationAccuracyMetres);
        Assert.InRange(result.Clinic.DistanceKilometres, 0, 100);
        Assert.InRange(result.Hospital.DistanceKilometres, 0, 100);
        Assert.Matches(@"^https://www.openstreetmap.org/(node|way|relation)/\d+$", result.Clinic.MapUrl);
        var unsupported = NearbyMedicalFacilities.Find(Location("ZA"))!;
        Assert.Null(unsupported.Clinic);
        Assert.Null(unsupported.Hospital);
    }

    private static LocationContext Location(string country) => new(country,
        new(null, null, PresenceState.Unknown), new(null, null, PresenceState.Unknown), new(null, null, PresenceState.Unknown),
        -17.83, 31.05, 500, LocationSource.Reporter, LocationPrecision.Approximate, null);
}
