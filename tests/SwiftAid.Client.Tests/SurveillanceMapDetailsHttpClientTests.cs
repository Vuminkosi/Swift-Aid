using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Client.Tests;

public sealed class SurveillanceMapDetailsHttpClientTests
{
    [Fact]
    public async Task ReadsAuthorizedDetailsUsingCountryAndPeriod()
    {
        var handler = new Handler(new([new("p", "Town", -26, 31, 5, 0)], [], false, 0));
        var result = await Create(handler).GetAsync("sz", MapAggregatePeriod.Days90);
        Assert.True(result.Succeeded);
        Assert.Equal("Town", Assert.Single(result.Value!.Points).Label);
        Assert.Equal("?country=SZ&period=days90", handler.Uri!.Query);
    }

    [Fact]
    public async Task RejectsUnsuppressedSmallGroupsAndUnsafeCaseLinks()
    {
        var smallGroup = new SurveillanceMapDetails([new("p", "Town", -26, 31, 4, 0)], [], false, 0);
        Assert.False((await Create(new Handler(smallGroup)).GetAsync("SZ", MapAggregatePeriod.Days30)).Succeeded);
        var signal = new SurveillanceMapSignal(Guid.NewGuid(), "Synthetic", "Area", "New", 5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "comparison", "https://other.test/");
        Assert.False((await Create(new Handler(new([], [signal], false, 0))).GetAsync("SZ", MapAggregatePeriod.Days30)).Succeeded);
        var allowed = signal with { CaseHref = "/case/" + Guid.NewGuid().ToString("D") };
        Assert.True((await Create(new Handler(new([], [allowed], false, 0))).GetAsync("SZ", MapAggregatePeriod.Days30)).Succeeded);
    }

    [Fact]
    public async Task DeniedAndInvalidInputsDoNotReturnData()
    {
        var handler = new Handler(new([], [], false, 0), HttpStatusCode.Forbidden);
        var client = Create(handler);
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.GetAsync("SZ", MapAggregatePeriod.Days30)).Code);
        handler.Uri = null;
        Assert.False((await client.GetAsync("SZZ", MapAggregatePeriod.Days30)).Succeeded);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task IncludesTheRevalidatedWorkspaceInMapRequests()
    {
        var handler = new Handler(new([], [], false, 0));
        var client = new SurveillanceMapDetailsHttpClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, new Workspace());
        Assert.True((await client.GetAsync("SZ", MapAggregatePeriod.Days30)).Succeeded);
        Assert.Contains("workspace=team%20b", handler.Uri!.Query);
    }

    private sealed class Workspace : IWorkspaceAccessClient
    {
        public Task<UiCapabilityResult<WorkspaceAccessState>> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UiCapabilityResult<WorkspaceAccessState>(UiCapabilityResultCode.Success,
                new([new("team b", "Team B")], "team b"), new("workspace", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true)));
        public Task<UiCapabilityResult<WorkspaceAccessState>> SelectWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) => GetStateAsync(cancellationToken);
    }

    private static SurveillanceMapDetailsHttpClient Create(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

    private sealed class Handler(SurveillanceMapDetails details, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(details) });
        }
    }
}
