using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Tests;

public sealed class StaffSessionHistoryHttpClientTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static SessionHistoryEntry Entry(long version) => new(version, "synthetic-actor", "synthetic reason",
        DateTimeOffset.UtcNow, null, "Draft", null, null);

    [Fact]
    public async Task ReadsBoundedPageAndCarriesCursor()
    {
        var handler = new Handler(new(SessionId, [Entry(4), Entry(3)], 3));
        var result = await Create(handler).ReadAsync(SessionId, 5);
        Assert.True(result.Succeeded);
        Assert.Equal(new long[] { 4, 3 }, result.Value!.Items.Select(item => item.Version));
        Assert.Contains("limit=25", handler.Uri!.Query);
        Assert.Contains("beforeVersion=5", handler.Uri.Query);
    }

    [Fact]
    public async Task RejectsWrongSessionOutOfOrderOrMalformedCursor()
    {
        Assert.False((await Create(new Handler(new(Guid.NewGuid(), [], null))).ReadAsync(SessionId, null)).Succeeded);
        Assert.False((await Create(new Handler(new(SessionId, [Entry(3), Entry(4)], null))).ReadAsync(SessionId, null)).Succeeded);
        Assert.False((await Create(new Handler(new(SessionId, [Entry(4)], 3))).ReadAsync(SessionId, null)).Succeeded);
        Assert.False((await Create(new Handler(new(SessionId, [Entry(5)], null))).ReadAsync(SessionId, 5)).Succeeded);
    }

    [Fact]
    public async Task DenialAndInvalidInputReturnNoHistory()
    {
        var handler = new Handler(new(SessionId, [Entry(1)], null), HttpStatusCode.NotFound);
        var client = Create(handler);
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.ReadAsync(SessionId, null)).Code);
        handler.Uri = null;
        Assert.Equal(UiCapabilityResultCode.InvalidRequest, (await client.ReadAsync(SessionId, 0)).Code);
        Assert.Null(handler.Uri);
    }

    private static StaffSessionHistoryHttpClient Create(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

    private sealed class Handler(SessionHistoryPage page, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(page) });
        }
    }
}
