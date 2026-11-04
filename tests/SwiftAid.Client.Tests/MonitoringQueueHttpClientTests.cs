using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

public sealed class MonitoringQueueHttpClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetAssigned_ReturnsQueueAndScope()
    {
        var item = new MonitoringQueueItem(
            Guid.NewGuid(), MonitoringQueueKind.FollowUp, Guid.NewGuid(), "revisit holding",
            "officer-1", DateTimeOffset.UtcNow.AddDays(1), "Scheduled", false, DateTimeOffset.UtcNow, 3);
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK,
            new MonitoringQueueResult(CaseQueueAccess.Allowed, [item])));
        var client = new MonitoringQueueHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        var result = await client.GetAssignedAsync(null);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal([item], result.Items);
        Assert.Equal("/api/v1/cases/monitoring/queue?scope=Assigned", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetOpen_AppendsLimit()
    {
        var handler = new CapturingHandler(JsonResponse(HttpStatusCode.OK,
            new MonitoringQueueResult(CaseQueueAccess.Allowed, [])));
        var client = new MonitoringQueueHttpClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://gateway.test/") });

        await client.GetOpenAsync(25);

        Assert.Equal("/api/v1/cases/monitoring/queue?scope=Open&limit=25", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Get_MapsDenialToDenied(HttpStatusCode status)
    {
        var client = Create(new HttpResponseMessage(status));

        var result = await client.GetAssignedAsync(null);

        Assert.Equal(CaseQueueAccess.Denied, result.Access);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Get_MapsFailureToUnavailable(HttpStatusCode status)
    {
        var client = Create(new HttpResponseMessage(status));

        var result = await client.GetOpenAsync(null);

        Assert.Equal(CaseQueueAccess.Unavailable, result.Access);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Get_WithoutBaseAddress_ReturnsUnavailable()
    {
        var client = new MonitoringQueueHttpClient(
            new HttpClient(new CapturingHandler(JsonResponse(HttpStatusCode.OK, new object()))));

        var result = await client.GetAssignedAsync(null);

        Assert.Equal(CaseQueueAccess.Unavailable, result.Access);
    }

    private static MonitoringQueueHttpClient Create(HttpResponseMessage response) =>
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
