using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Ui.Tests;

public sealed partial class EllaiSurfaceLogicTests
{
    private static UiIntentProposal CaseProposal => new(UiIntentKind.OpenAuthorizedRecord, "case-detail",
        "6d8cbf50-47f6-4b39-9ad8-7c2324401679", null, new Dictionary<string, string>());

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CaseNavigationRequiresNewCompletionAndFreshAccess(bool newlyCompleted, bool allowed)
    {
        var client = new NavigationCaseClient { Allowed = allowed };
        using var services = new ServiceCollection().AddSingleton<ICaseTimelineClient>(client).BuildServiceProvider();
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), "unsent draft");
        SetProperty(host, "Services", services);
        var navigation = new TabNavigationManager("dashboard");
        SetProperty(host, "Navigation", navigation);
        var turn = new EllaiConversationTurnDto("open-turn", 1, "Open the case", "Requested", "Completed", 1, DateTimeOffset.UtcNow)
        { UiProposals = [new("open-proposal", CaseProposal)] };
        SetField(host, "_lastDurableTurn", turn);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, newlyCompleted);
        Assert.Equal(newlyCompleted && allowed ? 1 : 0, navigation.Moves);
        Assert.Equal(newlyCompleted ? 1 : 0, client.Calls);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, newlyCompleted);
        Assert.Equal(newlyCompleted ? 1 : 0, client.Calls);
        Assert.Equal("unsent draft", GetField<string>(host, "InputText"));
    }

    [Theory]
    [InlineData("turn")]
    [InlineData("page")]
    [InlineData("session")]
    public async Task CaseNavigationRejectsStaleAuthorizationResult(string change)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new NavigationCaseClient { Wait = () => { started.SetResult(); return released.Task; } };
        using var services = new ServiceCollection().AddSingleton<ICaseTimelineClient>(client).BuildServiceProvider();
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        SetProperty(host, "Services", services);
        var navigation = new TabNavigationManager("dashboard");
        SetProperty(host, "Navigation", navigation);
        var turn = new EllaiConversationTurnDto("open-turn", 1, "Open case", "Requested", "Completed", 1, DateTimeOffset.UtcNow);
        SetField(host, "_lastDurableTurn", turn);
        SetField(host, "_lastCaseNavigationProposalId", "proposal");
        var pending = InvokeAsync(host, "OpenAuthorizedCaseAsync", "open-turn", "proposal", CaseProposal);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "turn") SetField(host, "_lastDurableTurn", turn with { ClientUtteranceId = "new-turn" });
        else if (change == "page") navigation.NavigateTo("/monitoring");
        else SetField(host, "_sessionView", null);
        released.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(change == "page" ? 1 : 0, navigation.Moves);
    }

    private sealed class NavigationCaseClient : ICaseTimelineClient
    {
        public bool Allowed { get; init; } = true;
        public Func<Task>? Wait { get; init; }
        public int Calls { get; private set; }
        public async Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default, long? beforeAggregateVersion = null)
        {
            Calls++;
            if (Wait is not null) await Wait();
            return Allowed ? new(CaseTimelineAccess.Found,
                new(new(caseId, "org", "workspace", "SZ", "Open", "actor", 1, 1, DateTimeOffset.UtcNow), []))
                : new(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }
    }
}
