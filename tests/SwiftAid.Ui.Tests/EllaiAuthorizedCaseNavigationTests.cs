using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiAuthorizedCaseNavigationTests
{
    private static readonly Guid CaseId = Guid.Parse("6d8cbf50-47f6-4b39-9ad8-7c2324401679");
    private static UiIntentProposal Proposal => new(UiIntentKind.OpenAuthorizedRecord, "case-detail", CaseId.ToString("D"), null, new Dictionary<string, string>());
    private static CaseTimelineResult Found(Guid id) => new(CaseTimelineAccess.Found,
        new(new(id, "org", "workspace", "SZ", "Open", "actor", 1, 1, DateTimeOffset.UtcNow), []));

    [Fact]
    public async Task FreshReadMustReturnTheExactRequestedCase()
    {
        var client = new Client { Result = Found(CaseId) };
        Assert.Equal($"/case/{CaseId:D}", await EllaiAuthorizedCaseNavigation.ResolveAsync(Proposal, client, CancellationToken.None));
        Assert.Equal(CaseId, client.Requested);
        Assert.Equal(1, client.Limit);
        client.Result = Found(Guid.NewGuid());
        Assert.Null(await EllaiAuthorizedCaseNavigation.ResolveAsync(Proposal, client, CancellationToken.None));
        client.Result = new(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        Assert.Null(await EllaiAuthorizedCaseNavigation.ResolveAsync(Proposal, client, CancellationToken.None));
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task InvalidProposalNeverQueriesApplicationData()
    {
        var client = new Client { Result = Found(CaseId) };
        Assert.Null(await EllaiAuthorizedCaseNavigation.ResolveAsync(Proposal with { ResourceId = "../admin" }, client, CancellationToken.None));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task CancellationAfterReadCannotProduceNavigation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Client { Result = Found(CaseId), AfterRead = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EllaiAuthorizedCaseNavigation.ResolveAsync(Proposal, client, cancellation.Token));
    }

    [Fact]
    public async Task MissingHeaderAndTransportFailureDoNotProduceRoutes()
    {
        var client = new Client { Result = new(CaseTimelineAccess.Found, null) };
        Assert.Null(await EllaiAuthorizedCaseNavigation.ResolveAsync(Proposal, client, CancellationToken.None));
        client.AfterRead = () => throw new HttpRequestException();
        await Assert.ThrowsAsync<HttpRequestException>(() => EllaiAuthorizedCaseNavigation.ResolveAsync(Proposal, client, CancellationToken.None));
    }

    private sealed class Client : ICaseTimelineClient
    {
        public CaseTimelineResult Result { get; set; } = new(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        public Action? AfterRead { get; set; }
        public int Calls { get; private set; }
        public Guid Requested { get; private set; }
        public int? Limit { get; private set; }
        public Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default, long? beforeAggregateVersion = null)
        {
            Calls++;
            Requested = caseId;
            Limit = limit;
            AfterRead?.Invoke();
            return Task.FromResult(Result);
        }
    }
}
