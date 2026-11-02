using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed partial class EllaiSurfaceLogicTests
{
    private static UiIntentProposal FeatureProposal => new(UiIntentKind.NavigateFeature, "navigation", null,
        "navigation.monitoring", new Dictionary<string, string>());

    [Theory]
    [InlineData(true, true, UiObservationState.Ready, true)]
    [InlineData(true, true, UiObservationState.Hidden, true)]
    [InlineData(true, true, UiObservationState.Disabled, false)]
    [InlineData(true, true, UiObservationState.Unmounted, false)]
    [InlineData(true, true, UiObservationState.Ambiguous, false)]
    [InlineData(false, true, UiObservationState.Ready, false)]
    [InlineData(true, false, UiObservationState.Ready, false)]
    public async Task DirectFeatureNavigationRequiresCurrentIdentityAndAvailableTarget(bool signedIn, bool newlyCompleted, UiObservationState state, bool moves)
    {
        var auth = new NavigationAuthentication { SignedIn = signedIn };
        using var services = new ServiceCollection().AddSingleton<AuthenticationStateProvider>(auth).BuildServiceProvider();
        using var host = await CreateHost(new FakeReviewClient { ObservationHandler = () => Task.FromResult(true) }, CreateSession(1), "draft");
        SetProperty(host, "Services", services);
        var navigation = new TabNavigationManager("dashboard");
        SetProperty(host, "Navigation", navigation);
        SetField(host, "_uiObservationModule", new NavigationObservationModule(state));
        var turn = new EllaiConversationTurnDto("navigate-turn", 1, "Open monitoring", "Requested", "Completed", 1, DateTimeOffset.UtcNow)
        { UiProposals = [new("navigation-proposal", FeatureProposal)] };
        SetField(host, "_lastDurableTurn", turn);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, newlyCompleted);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, newlyCompleted);
        Assert.Equal(moves ? 1 : 0, navigation.Moves);
        if (moves) Assert.EndsWith("/monitoring", navigation.Uri);
        Assert.Equal("draft", GetField<string>(host, "InputText"));
    }

    [Fact]
    public async Task SignOutDuringObservationPreventsNavigation()
    {
        var auth = new NavigationAuthentication { SignedIn = true };
        using var services = new ServiceCollection().AddSingleton<AuthenticationStateProvider>(auth).BuildServiceProvider();
        using var host = await CreateHost(new FakeReviewClient { ObservationHandler = () => { auth.SignedIn = false; return Task.FromResult(true); } }, CreateSession(1), string.Empty);
        SetProperty(host, "Services", services);
        var navigation = new TabNavigationManager("dashboard");
        SetProperty(host, "Navigation", navigation);
        SetField(host, "_uiObservationModule", new NavigationObservationModule(UiObservationState.Ready));
        SetField(host, "_lastDurableTurn", new EllaiConversationTurnDto("turn", 1, "Open monitoring", "Requested", "Completed", 1, DateTimeOffset.UtcNow));
        SetField(host, "_lastFeatureNavigationProposalId", "proposal");
        await InvokeAsync(host, "NavigateFeatureAsync", "turn", "proposal", FeatureProposal);
        Assert.Equal(0, navigation.Moves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackToCaseRepeatsAuthorization(bool allowed)
    {
        var client = new NavigationCaseClient { Allowed = allowed };
        using var services = new ServiceCollection().AddSingleton<AuthenticationStateProvider>(new NavigationAuthentication { SignedIn = true })
            .AddSingleton<ICaseTimelineClient>(client).BuildServiceProvider();
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        SetProperty(host, "Services", services);
        var navigation = new TabNavigationManager("dashboard");
        SetProperty(host, "Navigation", navigation);
        var trail = GetField<EllaiNavigationTrail>(host, "_navigationTrail");
        trail.Record("case/6d8cbf50-47f6-4b39-9ad8-7c2324401679"); trail.Record("dashboard");
        SetField(host, "_lastDurableTurn", new EllaiConversationTurnDto("turn", 1, "Go back", "Requested", "Completed", 1, DateTimeOffset.UtcNow));
        SetField(host, "_lastFeatureNavigationProposalId", "proposal");
        var proposal = new UiIntentProposal(UiIntentKind.NavigateFeature, "navigation", null, null, new Dictionary<string, string> { ["command"] = "back" });
        await InvokeAsync(host, "NavigateFeatureAsync", "turn", "proposal", proposal);
        Assert.Equal(1, client.Calls);
        Assert.Equal(allowed ? 1 : 0, navigation.Moves);
    }

    [Fact]
    public async Task DirectNavigationCanRevealALateMountedMenu()
    {
        using var services = new ServiceCollection().AddSingleton<AuthenticationStateProvider>(new NavigationAuthentication { SignedIn = true }).BuildServiceProvider();
        using var host = await CreateHost(new FakeReviewClient { ObservationHandler = () => Task.FromResult(true) }, CreateSession(1), string.Empty);
        SetProperty(host, "Services", services);
        var navigation = new TabNavigationManager("dashboard");
        SetProperty(host, "Navigation", navigation);
        var module = new NavigationObservationModule(UiObservationState.Unmounted);
        SetField(host, "_uiObservationModule", module);
        var reveals = 0;
        SetProperty(host, "RevealNavigation", Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () =>
        { reveals++; module.State = UiObservationState.Ready; }));
        SetField(host, "_lastDurableTurn", new EllaiConversationTurnDto("turn", 1, "Open monitoring", "Requested", "Completed", 1, DateTimeOffset.UtcNow));
        SetField(host, "_lastFeatureNavigationProposalId", "proposal");
        await InvokeAsync(host, "NavigateFeatureAsync", "turn", "proposal", FeatureProposal);
        Assert.Equal(1, reveals);
        Assert.Equal(1, navigation.Moves);
    }

    [Fact]
    public async Task NewTurnFencesPendingFeatureNavigation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = new ServiceCollection().AddSingleton<AuthenticationStateProvider>(new NavigationAuthentication { SignedIn = true }).BuildServiceProvider();
        using var host = await CreateHost(new FakeReviewClient { ObservationHandler = () => { started.SetResult(); return release.Task; } }, CreateSession(1), string.Empty);
        SetProperty(host, "Services", services);
        var navigation = new TabNavigationManager("dashboard");
        SetProperty(host, "Navigation", navigation);
        SetField(host, "_uiObservationModule", new NavigationObservationModule(UiObservationState.Ready));
        var turn = new EllaiConversationTurnDto("turn", 1, "Open monitoring", "Requested", "Completed", 1, DateTimeOffset.UtcNow);
        SetField(host, "_lastDurableTurn", turn);
        SetField(host, "_lastFeatureNavigationProposalId", "proposal");
        var pending = InvokeAsync(host, "NavigateFeatureAsync", "turn", "proposal", FeatureProposal);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SetField(host, "_lastDurableTurn", turn with { ClientUtteranceId = "new-turn" });
        release.SetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, navigation.Moves);
    }

    private sealed class NavigationAuthentication : AuthenticationStateProvider
    {
        public bool SignedIn { get; set; }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(SignedIn ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "actor")], "test") : new ClaimsIdentity())));
    }
    private sealed class NavigationObservationModule(UiObservationState state) : IJSObjectReference
    {
        public UiObservationState State { get; set; } = state;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)new UiObservationReport("view", "context", "web", ["navigation.monitoring"], [new("navigation.monitoring", State)]));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
