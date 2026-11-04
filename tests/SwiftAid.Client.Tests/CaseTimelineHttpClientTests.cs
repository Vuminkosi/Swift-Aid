using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

public sealed class CaseTimelineHttpClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get_ReturnsTimelineWhenFound()
    {
        var header = new CaseTimelineHeader(
            Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 3, 1,
            DateTimeOffset.UtcNow);
        var timeline = new CaseTimeline(header, []);
        var client = Create(JsonResponse(HttpStatusCode.OK, timeline));

        var result = await client.GetAsync(header.CaseId, null);

        Assert.Equal(CaseTimelineAccess.Found, result.Access);
        Assert.Equal(header.CaseId, result.Timeline!.Header.CaseId);
    }

    [Fact]
    public async Task Get_RejectsTimelineForAnotherCase()
    {
        var header = new CaseTimelineHeader(
            Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 3, 1,
            DateTimeOffset.UtcNow);
        var client = Create(JsonResponse(HttpStatusCode.OK, new CaseTimeline(header, [])));

        var result = await client.GetAsync(Guid.NewGuid(), null);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Timeline);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Get_MapsFailureToNotFound(HttpStatusCode status)
    {
        var client = Create(new HttpResponseMessage(status));

        var result = await client.GetAsync(Guid.NewGuid(), null);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Timeline);
    }

    [Fact]
    public async Task Get_ReturnsNotFoundOnInvalidJson()
    {
        var handler = new CapturingHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{bad json") });
        var client = new CaseTimelineHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.GetAsync(Guid.NewGuid(), null);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
    }

    [Fact]
    public async Task Get_RejectsInvalidInputLocally()
    {
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK, new CaseTimeline(
            new CaseTimelineHeader(Guid.NewGuid(), "o", "w", "ZW", "Active", "a", 1, 1, DateTimeOffset.UtcNow), [])));
        var client = new CaseTimelineHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.GetAsync(Guid.Empty, null);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Get_WithoutBaseAddress_ReturnsNotFound()
    {
        var client = new CaseTimelineHttpClient(
            new HttpClient(new CapturingHandler(JsonResponse(HttpStatusCode.OK, new object()))));

        var result = await client.GetAsync(Guid.NewGuid(), null);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
    }

    [Fact]
    public async Task Get_AppendsLimitAndCaseRoute()
    {
        var header = new CaseTimelineHeader(
            Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 1, 1,
            DateTimeOffset.UtcNow);
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK, new CaseTimeline(header, [])));
        var client = new CaseTimelineHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        await client.GetAsync(header.CaseId, 25, beforeAggregateVersion: 12);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            $"/api/v1/cases/{header.CaseId:D}/timeline?limit=25&beforeAggregateVersion=12",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    private static CaseTimelineHttpClient Create(HttpResponseMessage response) =>
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
