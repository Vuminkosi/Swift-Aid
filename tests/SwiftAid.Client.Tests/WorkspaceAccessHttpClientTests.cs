using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class WorkspaceAccessHttpClientTests
{
    [Fact]
    public async Task SelectionIsRevalidatedAndRevocationClearsIt()
    {
        var handler = new Handler();
        var client = new WorkspaceAccessHttpClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });
        Assert.Equal("a", (await client.GetStateAsync()).Value?.SelectedWorkspaceId);
        Assert.Equal("b", (await client.SelectWorkspaceAsync("b")).Value?.SelectedWorkspaceId);
        Assert.Equal("b", (await client.GetStateAsync()).Value?.SelectedWorkspaceId);
        Assert.Equal("?workspace=b", handler.LastQuery);
        handler.Revoked = true;
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.GetStateAsync()).Code);
        handler.Revoked = false;
        Assert.Equal("a", (await client.GetStateAsync()).Value?.SelectedWorkspaceId);
        Assert.Equal("", handler.LastQuery);
    }

    [Fact]
    public async Task SelectionMustMatchAWorkspaceReturnedByTheServer()
    {
        var handler = new Handler { Malformed = true };
        var client = new WorkspaceAccessHttpClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });
        Assert.False((await client.SelectWorkspaceAsync("b")).Succeeded);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool Revoked { get; set; }
        public bool Malformed { get; set; }
        public string? LastQuery { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastQuery = request.RequestUri!.Query;
            if (Revoked) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var selected = LastQuery.Contains("workspace=b", StringComparison.Ordinal) ? "b" : "a";
            var options = Malformed ? new[] { new WorkspaceAccessOption("a", "A") } :
                [new WorkspaceAccessOption("a", "A"), new WorkspaceAccessOption("b", "B")];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkspaceAccessState(options, selected)) });
        }
    }
}
