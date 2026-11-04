using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Client.Tests;

public sealed class MapAggregateHttpClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset AsOf = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetAsync_MapsLiveAggregateToPresentationPage()
    {
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK, AllowedResult()));
        var client = new Presentation.MapAggregateHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.GetAsync(
            new Presentation.MapAggregateQuery(new Presentation.PageRequest(1, 100), null,
                Presentation.MapAggregatePeriod.Days30));

        Assert.Equal(Presentation.UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        var area = Assert.Single(result.Value!.Areas.Items);
        Assert.Equal("SZ", area.AreaCode);
        Assert.Equal("Eswatini", area.AreaLabel);
        Assert.Equal(5, area.HumanCount);
        Assert.Equal("https://gateway.test/api/v1/surveillance/area-aggregates?period=days30",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetAsync_ForwardsPeriodAndRegion()
    {
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK, AllowedResult()));
        var client = new Presentation.MapAggregateHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        await client.GetAsync(
            new Presentation.MapAggregateQuery(new Presentation.PageRequest(1, 100), "SZ-HH",
                Presentation.MapAggregatePeriod.Days90));

        Assert.Equal(
            "https://gateway.test/api/v1/surveillance/area-aggregates?period=days90&regionCode=SZ-HH",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, Presentation.UiCapabilityResultCode.Denied)]
    [InlineData(HttpStatusCode.Forbidden, Presentation.UiCapabilityResultCode.Denied)]
    [InlineData(HttpStatusCode.NotFound, Presentation.UiCapabilityResultCode.Denied)]
    [InlineData(HttpStatusCode.BadRequest, Presentation.UiCapabilityResultCode.InvalidRequest)]
    [InlineData(HttpStatusCode.InternalServerError, Presentation.UiCapabilityResultCode.Unavailable)]
    public async Task GetAsync_MapsStatusCodes(HttpStatusCode status, Presentation.UiCapabilityResultCode expected)
    {
        var client = Create(new HttpResponseMessage(status));

        var result = await client.GetAsync(
            new Presentation.MapAggregateQuery(new Presentation.PageRequest(1, 100)));

        Assert.Equal(expected, result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetAsync_WithoutBaseAddress_ReturnsUnavailable()
    {
        var client = new Presentation.MapAggregateHttpClient(
            new HttpClient(new CapturingHandler(JsonResponse(HttpStatusCode.OK, AllowedResult()))));

        var result = await client.GetAsync(
            new Presentation.MapAggregateQuery(new Presentation.PageRequest(1, 100)));

        Assert.Equal(Presentation.UiCapabilityResultCode.Unavailable, result.Code);
    }

    [Fact]
    public async Task GetAsync_InvalidWirePayload_ReturnsUnavailable()
    {
        var invalid = new SurveillanceAggregateResult(
            SurveillanceAggregateAccess.Allowed,
            [new SurveillanceAggregateArea(
                "SZ", "Eswatini", SurveillanceAggregatePrecision.Country,
                5, 5, 10, true, "Small counts are withheld to protect privacy.", AsOf)],
            AsOf,
            false);
        var client = Create(JsonResponse(HttpStatusCode.OK, invalid));

        var result = await client.GetAsync(
            new Presentation.MapAggregateQuery(new Presentation.PageRequest(1, 100)));

        Assert.Equal(Presentation.UiCapabilityResultCode.Unavailable, result.Code);
        Assert.Null(result.Value);
    }

    private static SurveillanceAggregateResult AllowedResult() => new(
        SurveillanceAggregateAccess.Allowed,
        [new SurveillanceAggregateArea(
            "SZ",
            "Eswatini",
            SurveillanceAggregatePrecision.Country,
            5,
            5,
            10,
            false,
            null,
            AsOf)],
        AsOf,
        false);

    private static Presentation.MapAggregateHttpClient Create(HttpResponseMessage response) =>
        new(new HttpClient(new CapturingHandler(response)) { BaseAddress = new Uri("https://gateway.test/") });

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T value) =>
        new(status) { Content = JsonContent.Create(value, options: JsonOptions) };

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }
}
