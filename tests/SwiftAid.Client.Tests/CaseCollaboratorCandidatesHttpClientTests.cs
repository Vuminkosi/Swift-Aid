using System.Net;
using System.Net.Http.Json;
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

public sealed class CaseCollaboratorCandidatesHttpClientTests
{
    [Fact]
    public async Task Get_PreservesCandidateScopeAndEscapesSearchAndCursor()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new CaseCollaboratorCandidatePage(
                [new("scope-1", "actor-1", "Colleague", ["ReadCase", "ManageFollowUp"])], "scope-next"))
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://gateway.test/") };

        var page = await new CaseCollaboratorCandidatesHttpClient(http).GetAsync(id, "A&B +", "scope/1?x=2");

        Assert.NotNull(page);
        Assert.Equal("scope-next", page.NextCursor);
        var candidate = Assert.Single(page.Items);
        Assert.Equal("actor-1", candidate.ActorId);
        Assert.Equal(["ReadCase", "ManageFollowUp"], candidate.Operations);
        Assert.Equal($"/api/v1/cases/{id:D}/collaborator-candidates?limit=25&search=A%26B%20%2B&after=scope%2F1%3Fx%3D2&followUpAssignees=false", handler.Path);
    }

    [Fact]
    public async Task GetFollowUpAssignees_requests_the_narrow_authorized_mode()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(new(HttpStatusCode.OK) { Content = JsonContent.Create(new CaseCollaboratorCandidatePage([], null)) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://gateway.test/") };

        await new CaseCollaboratorCandidatesHttpClient(http).GetAsync(id, "staff", null, followUpAssignees: true);

        Assert.EndsWith("&followUpAssignees=true", handler.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Get_DoesNotDiscloseDeniedCandidates(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(new(status))) { BaseAddress = new("https://gateway.test/") };
        Assert.Null(await new CaseCollaboratorCandidatesHttpClient(http).GetAsync(Guid.NewGuid(), null, null));
    }

    [Fact]
    public async Task Get_ServerFailureIsNotAnEmptyEligibleList()
    {
        using var http = new HttpClient(new Handler(new(HttpStatusCode.ServiceUnavailable))) { BaseAddress = new("https://gateway.test/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => new CaseCollaboratorCandidatesHttpClient(http).GetAsync(Guid.NewGuid(), null, null));
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.PathAndQuery;
            return Task.FromResult(response);
        }
    }
}
