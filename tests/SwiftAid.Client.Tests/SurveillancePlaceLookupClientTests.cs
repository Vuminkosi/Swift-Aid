using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.Client.Tests;

public sealed class SurveillancePlaceLookupClientTests
{
    private static readonly GeoAdminAncestor Region = new("region-1", "Example region", GeoLocationKind.Region);
    private static readonly GeoSearchResult Place = new("place-1", "Example town", GeoLocationKind.Settlement,
        "SZ", "Eswatini", "Example region", -26.3, 31.1, false, [Region]);

    [Fact]
    public async Task FindsOnlyTheMatchingPublicRegistryPlace()
    {
        var handler = new StubHandler([Place]);
        var client = Create(handler);
        var result = await client.FindAsync("SZ", "place-1", "Example region", "Example town");
        Assert.Equal(Place.Id, result?.Id);
        Assert.Contains("country=SZ", handler.RequestUri!.Query);
        Assert.Contains("q=Example%20town", handler.RequestUri.Query);
    }

    [Fact]
    public async Task RejectsAmbiguousWrongCountryAndInvalidCoordinates()
    {
        Assert.Null(await Create(new StubHandler([Place, Place])).FindAsync("SZ", null, "Example region", "Example town"));
        Assert.Null(await Create(new StubHandler([Place with { CountryCode = "ZA" }])).FindAsync("SZ", "place-1", "Example region", "Example town"));
        Assert.Null(await Create(new StubHandler([Place with { Latitude = 91 }])).FindAsync("SZ", "place-1", "Example region", "Example town"));
        Assert.Null(await Create(new StubHandler([Place])).FindAsync("SZ", "place-1", "Other region", "Example town"));
        Assert.Null(await Create(new StubHandler([Place], totalCount: 21)).FindAsync("SZ", null, "Example region", "Example town"));
        Assert.NotNull(await Create(new StubHandler([Place], totalCount: 21)).FindAsync("SZ", "place-1", "Example region", "Example town"));
    }

    [Fact]
    public async Task InvalidInputAndUnavailableServiceDoNotInventAPlace()
    {
        var handler = new StubHandler([Place]);
        Assert.Null(await Create(handler).FindAsync("SZZ", "place-1", "Example region", "Example town"));
        Assert.Null(handler.RequestUri);
        Assert.Null(await Create(new StubHandler([], HttpStatusCode.ServiceUnavailable)).FindAsync("SZ", null, "Example region", "Example town"));
    }

    private static SurveillancePlaceLookupClient Create(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

    private sealed class StubHandler(GeoSearchResult[] results, HttpStatusCode status = HttpStatusCode.OK, int? totalCount = null) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new GeoSearchResponse("fixture", totalCount ?? results.Length, results))
            });
        }
    }
}
