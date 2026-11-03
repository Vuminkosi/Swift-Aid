using System.Security.Claims;
using System.Reflection;
using Bit.Bonboarding;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;
using SwiftAid.UI.Ellai.Components;
using SwiftAid.UI.Ellai.Guidance;
using SwiftAid.UI.Components.Layout;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T13")]
public sealed partial class EllaiSurfaceLogicTests
{
    private static EllaiConversationTurnDto GuidanceTurn() => new("guide-turn", 1, "Show the report form", "Let me show you.", "Completed", 1, DateTimeOffset.UtcNow)
    {
        UiProposals = [new("proposal-1", new(UiIntentKind.StartGuidance, "ellai.guidance", null, null,
            new Dictionary<string, string> { ["feature"] = "report", ["objective"] = "Open report form" }))]
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyServerAcceptedUiObservationBecomesGuidanceEvidence(bool accepted)
    {
        var client = new FakeReviewClient { ObservationHandler = () => Task.FromResult(accepted) };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        SetField(host, "_uiObservationModule", new ObservationJsModule());
        await InvokeAsync(host, "ReportCurrentUiAsync", CancellationToken.None);
        Assert.Equal(accepted, GetField<UiObservationReport?>(host, "_latestGuidanceObservation") is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuidanceObserverRecoversAfterTransportFailureOrTimeout(bool timeout)
    {
        var calls = 0;
        var client = new FakeReviewClient { ObservationHandler = () =>
        {
            if (++calls == 1) return Task.FromException<bool>(timeout ? new TaskCanceledException() : new HttpRequestException());
            return Task.FromResult(true);
        } };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        SetField(host, "_uiObservationModule", new ObservationJsModule());
        await InvokeAsync(host, "ObserveGuidanceIterationAsync", CancellationToken.None);
        Assert.Null(GetField<UiObservationReport?>(host, "_latestGuidanceObservation"));
        await InvokeAsync(host, "ObserveGuidanceIterationAsync", CancellationToken.None);
        Assert.NotNull(GetField<UiObservationReport?>(host, "_latestGuidanceObservation"));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(true, true, true, 1)]
    [InlineData(false, true, true, 0)]
    [InlineData(true, false, true, 0)]
    [InlineData(true, true, false, 0)]
    public async Task CurrentCaseTabSelectionRequiresFreshAcceptedObservationAndNeverReplays(bool fresh, bool accepted, bool casePage, int moves)
    {
        var client = new FakeReviewClient { ObservationHandler = () => Task.FromResult(accepted) };
        using var host = await CreateHost(client, CreateSession(1), "unsent draft");
        var navigation = new TabNavigationManager(casePage ? "case/6d8cbf50-47f6-4b39-9ad8-7c2324401679?keep=value" : "dashboard");
        SetProperty(host, "Navigation", navigation);
        SetField(host, "_uiObservationModule", new ObservationJsModule { ControlId = "case-detail.tabs" });
        var turn = new EllaiConversationTurnDto("tab-turn", 2, "Show attachments", "Opening attachments", "Completed", 1, DateTimeOffset.UtcNow)
        {
            UiProposals = [new("tab-proposal", new(UiIntentKind.SelectTab, "case-detail", null, "case-detail.tabs",
                new Dictionary<string, string> { ["tab"] = "attachments" }))],
        };
        SetField(host, "_lastDurableTurn", turn);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, fresh);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, fresh);
        Assert.Equal(moves, navigation.Moves);
        if (moves == 1)
        {
            Assert.Contains("/case/6d8cbf50-47f6-4b39-9ad8-7c2324401679?", navigation.Uri);
            Assert.Contains("keep=value", navigation.Uri);
            Assert.Contains("tab=attachments", navigation.Uri);
        }
        Assert.Equal("unsent draft", GetField<string>(host, "InputText"));
    }

    [Fact]
    public async Task NewerConversationTurnFencesPendingTabSelection()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient { ObservationHandler = () => { started.SetResult(); return release.Task; } };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        var navigation = new TabNavigationManager("case/6d8cbf50-47f6-4b39-9ad8-7c2324401679");
        SetProperty(host, "Navigation", navigation);
        SetField(host, "_uiObservationModule", new ObservationJsModule { ControlId = "case-detail.tabs" });
        var turn = new EllaiConversationTurnDto("old-turn", 1, "Show attachments", "Requested", "Completed", 1, DateTimeOffset.UtcNow);
        SetField(host, "_lastDurableTurn", turn);
        SetField(host, "_lastTabSelectionProposalId", "proposal");
        var pending = InvokeAsync(host, "SelectCurrentCaseTabAsync", "old-turn", "proposal", "attachments");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SetField(host, "_lastDurableTurn", turn with { ClientUtteranceId = "new-turn", Sequence = 2 });
        release.SetResult(true);
        await pending;
        Assert.Equal(0, navigation.Moves);
    }

    [Fact]
    public async Task SavedGuidanceIsOfferedWithoutExecutionAndSurvivesRestoredConversationHistory()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        var view = CreateSession(1) with { Guidance = new("saved-proposal", "saved-plan", "report", "Paused", 4) };
        await InvokeAsync(host, "OfferSavedGuidance", view);
        Assert.Equal("saved-proposal", GetField<string?>(host, "_guidanceProposalId"));
        Assert.True(GetField<bool>(host, "_guidanceProposalIsAccepted"));
        Assert.Equal(0, client.GuidancePlanCalls);
        await InvokeAsync(host, "ObserveGuidanceProposal", new EllaiConversationTurnDto("followup", 2,
            "A follow-up question", "An explanation", "Completed", 1, DateTimeOffset.UtcNow), false);
        Assert.Equal("saved-proposal", GetField<string?>(host, "_guidanceProposalId"));
        Assert.Equal(0, client.GuidancePlanCalls);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), false);
        Assert.Equal("proposal-1", GetField<string?>(host, "_guidanceProposalId"));
        Assert.False(GetField<bool>(host, "_guidanceProposalIsAccepted"));
    }

    [Theory]
    [InlineData("resume", 1)]
    [InlineData("pause", 0)]
    [InlineData("cancel", 0)]
    public async Task DetachedGuidanceControlsUseFreshOwnedCheckpoint(string command, int planCalls)
    {
        var client = new FakeReviewClient
        {
            ResumeResult = new(EllaiClientOperationStatus.Accepted,
                CreateSession(1) with { Guidance = new("saved-proposal", "saved-plan", "report", "Paused", 4) })
        };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        await InvokeAsync(host, "DispatchConversationalGuidanceControlAsync", command);
        Assert.Equal(planCalls, client.GuidancePlanCalls);
        if (command == "resume") Assert.Empty(client.GuidanceControls);
        else Assert.Equal(new EllaiGuidanceControlRequest("saved-proposal", "saved-plan", 4, command),
            Assert.Single(client.GuidanceControls));
        if (command == "cancel") Assert.Null(GetField<string?>(host, "_guidanceProposalId"));
    }

    [Fact]
    public async Task DetachedGuidanceDoesNotExecuteWithoutOwnedResumeMetadata()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        await InvokeAsync(host, "DispatchConversationalGuidanceControlAsync", "resume");
        Assert.Equal(0, client.GuidancePlanCalls);
        Assert.Empty(client.GuidanceControls);
    }

    [Theory]
    [InlineData("report.subject", PlanStepBehavior.GuideHuman, BonboardingTourStatus.Running, true, true)]
    [InlineData("report.narrative", PlanStepBehavior.GuideHuman, BonboardingTourStatus.Waiting, true, true)]
    [InlineData("report.subject", PlanStepBehavior.GuideHuman, BonboardingTourStatus.Paused, true, false)]
    [InlineData("report.subject", PlanStepBehavior.GuideHuman, BonboardingTourStatus.Running, false, false)]
    [InlineData("report.subject", PlanStepBehavior.Explain, BonboardingTourStatus.Running, true, false)]
    [InlineData("report.submit", PlanStepBehavior.GuideHuman, BonboardingTourStatus.Running, true, false)]
    [InlineData("report.confirmations", PlanStepBehavior.GuideHuman, BonboardingTourStatus.Running, true, true)]
    [InlineData("report.confirmations", PlanStepBehavior.GuideHuman, BonboardingTourStatus.Running, false, false)]
    public async Task AcceptedInputAdvancesThroughBonboardingWithoutSkippingItsProofCallback(
        string controlId, PlanStepBehavior behavior, BonboardingTourStatus status, bool accepted, bool advances)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = status;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "step" };
        var step = new GuidancePlanStep("step", controlId, "objective", behavior, null, null, [], [], "Complete this step.", null);
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["step"], [], [], [step], [], "catalogue", "contract"));
        SetField(host, "_latestGuidanceObservation", new UiObservationReport("view", "context", "web", [],
            CompletedControlIds: accepted ? [controlId] : []));
        await InvokeAsync(host, "AdvanceValidatedInputAsync", CancellationToken.None);
        Assert.Equal(advances ? new[] { "NextAsync" } : Array.Empty<string>(), proxy.Calls);
    }

    [Theory]
    [InlineData(true, BonboardingTourStatus.Running, true)]
    [InlineData(false, BonboardingTourStatus.Running, false)]
    [InlineData(true, BonboardingTourStatus.Paused, false)]
    public async Task DisplayedReceiptRequestsProofCheckedAdvanceWithoutRestartingConversation(bool displayed, BonboardingTourStatus status, bool advances)
    {
        var context = new EllaiDisplayedReceiptContext();
        if (displayed) context.Update(this, new SwiftAid.Contracts.Intake.IntakeReceipt(Guid.NewGuid(), Guid.NewGuid(),
            SwiftAid.Contracts.Intake.IntakeStatus.ReceivedUnverified, SwiftAid.Contracts.Intake.IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow));
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), "preserved draft", context);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = status;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "submit" };
        var step = new GuidancePlanStep("submit", "report.submit", "submit-report", PlanStepBehavior.GuideHuman,
            null, null, [], [], "Submit using the report form.", null);
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["submit"], [], [], [step], [], "catalogue", "contract"));
        await InvokeAsync(host, "AdvanceValidatedInputAsync", CancellationToken.None);
        Assert.Equal(advances ? new[] { "NextAsync" } : Array.Empty<string>(), proxy.Calls);
        Assert.Equal("preserved draft", GetField<string>(host, "InputText"));
        Assert.Equal("session-1", GetField<EllaiSessionView>(host, "_sessionView").SessionId);
    }

    [Fact]
    public async Task RestoredGuidanceOffersActionWithoutAutomaticallyDispatching()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), false);
        Assert.Equal("proposal-1", GetField<string?>(host, "_guidanceProposalId"));
        Assert.Equal(0, client.GuidancePlanCalls);
    }

    [Fact]
    public async Task DuplicateGuidanceEventsDoNotStartConcurrentPlanning()
    {
        var pending = new TaskCompletionSource<EllaiGuidancePlanDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient { GuidancePlanHandler = () => pending.Task };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), true);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), true);
        await InvokeAsync(host, "StartProposedGuidanceAsync");
        Assert.Equal(1, client.GuidancePlanCalls);
        pending.SetResult(new("NeedsObservation"));
        await host.Dispatcher.InvokeAsync(() => Task.CompletedTask);
    }

    [Fact]
    public async Task ObservedPendingTurnStartsGuidanceEvenAfterResponseTextSettlesThinking()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn() with { Outcome = "Processing" }, false);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), false);
        Assert.Equal(1, client.GuidancePlanCalls);
    }

    [Fact]
    public async Task NewGuidanceRequestDuringPlanningIsNotLostWhenOlderPlanFinishes()
    {
        var pending = new TaskCompletionSource<EllaiGuidancePlanDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new FakeReviewClient { GuidancePlanHandler = () => ++calls == 1
            ? pending.Task : Task.FromResult<EllaiGuidancePlanDto?>(new("NeedsObservation")) };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), true);
        var replacement = GuidanceTurn() with { ClientUtteranceId = "new-turn", Sequence = 2,
            UiProposals = [GuidanceTurn().UiProposals[0] with { ProposalId = "proposal-2" }] };
        await InvokeAsync(host, "ObserveGuidanceProposal", replacement, true);
        Assert.Equal(1, client.GuidancePlanCalls);
        pending.SetResult(new("NeedsObservation"));
        for (var attempt = 0; attempt < 30 && client.GuidancePlanCalls < 2; attempt++)
        {
            await Task.Delay(10);
            await host.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        }
        Assert.Equal(2, client.GuidancePlanCalls);
        Assert.Equal("proposal-2", GetField<string?>(host, "_guidanceProposalId"));
    }

    [Fact]
    public async Task ActiveGuidanceSurvivesAnOrdinaryFollowupButNewGuidanceReplacesIt()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), false);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        SetField(host, "_generatedTour", tour);
        await InvokeAsync(host, "ObserveGuidanceProposal",
            new EllaiConversationTurnDto("question", 2, "Why is this field needed?", null, "Processing", 0, null), false);
        Assert.Same(tour, GetField<IBonboardingTourSession?>(host, "_generatedTour"));
        Assert.Equal("proposal-1", GetField<string?>(host, "_guidanceProposalId"));
        await InvokeAsync(host, "ObserveGuidanceProposal",
            new EllaiConversationTurnDto("question", 2, "Why is this field needed?", "An explanation", "Completed", 1, DateTimeOffset.UtcNow), false);
        Assert.Same(tour, GetField<IBonboardingTourSession?>(host, "_generatedTour"));
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn() with
        {
            ClientUtteranceId = "different-tour", Sequence = 3,
            UiProposals = [GuidanceTurn().UiProposals[0] with { ProposalId = "proposal-2" }],
        }, false);
        Assert.Null(GetField<IBonboardingTourSession?>(host, "_generatedTour"));
        Assert.Equal("proposal-2", GetField<string?>(host, "_guidanceProposalId"));
    }

    [Theory]
    [InlineData("pause", true, true, "PauseAsync")]
    [InlineData("resume", true, true, "ResumeAsync")]
    [InlineData("cancel", true, true, "CancelAsync")]
    [InlineData("pause", false, true, null)]
    [InlineData("pause", true, false, null)]
    [InlineData("approve", true, true, null)]
    [InlineData("stop-speech", true, true, null)]
    [InlineData("next", true, true, "NextAsync")]
    [InlineData("next", false, true, null)]
    [InlineData("back", false, true, null)]
    [InlineData("back", true, false, null)]
    [InlineData("skip", false, true, null)]
    [InlineData("skip", true, false, null)]
    public async Task ConversationalGuidanceControlUsesDurableBoundaryAndNeverReplaysHistory(
        string command, bool fresh, bool persisted, string? expectedMethod)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), false);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        SetField(host, "_generatedTour", tour);
        proxy.TourStatus = BonboardingTourStatus.Running;
        var commands = new List<string>();
        SetField(host, "_controlGeneratedTour", (Func<string, Task<bool>>)(value =>
        {
            commands.Add(value);
            return Task.FromResult(persisted);
        }));
        var turn = new EllaiConversationTurnDto("control-turn", 2, "Control guidance", "Requested", "Completed", 1, DateTimeOffset.UtcNow)
        {
            UiProposals = [new("control-proposal", new(UiIntentKind.ControlGuidance, "ellai.guidance", null, null,
                new Dictionary<string, string> { ["command"] = command }))],
        };
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, fresh);
        await InvokeAsync(host, "ObserveGuidanceProposal", turn, fresh);
        if (expectedMethod is null) Assert.Empty(proxy.Calls);
        else Assert.Equal(expectedMethod, Assert.Single(proxy.Calls));
        Assert.Equal(fresh && command is "pause" or "resume" or "cancel" or "back" or "skip" ? 1 : 0, commands.Count);
        Assert.Same(tour, GetField<IBonboardingTourSession?>(host, "_generatedTour"));
    }

    [Theory]
    [InlineData("back", false)]
    [InlineData("back", true)]
    [InlineData("skip", false)]
    [InlineData("skip", true)]
    public async Task StepMoveOnlyClosesCurrentPresentationAfterDurableAcceptance(string requested, bool accepted)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        SetField(host, "_generatedTour", tour);
        var calls = 0;
        SetField(host, "_controlGeneratedTour", (Func<string, Task<bool>>)(command =>
        {
            Assert.Equal(requested, command);
            Assert.Empty(proxy.Calls);
            calls++;
            return Task.FromResult(accepted);
        }));
        await InvokeAsync(host, "DispatchConversationalGuidanceControlAsync", requested);
        Assert.Equal(1, calls);
        Assert.Equal(accepted ? new[] { "CancelAsync" } : Array.Empty<string>(), proxy.Calls);
        Assert.Equal(!accepted, ReferenceEquals(tour, GetField<IBonboardingTourSession?>(host, "_generatedTour")));
    }

    [Fact]
    public async Task ConcurrentConversationalNextRequestsCannotAdvanceTwice()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.NextHandler = () => { started.SetResult(); return release.Task; };
        SetField(host, "_generatedTour", tour);
        var first = InvokeAsync(host, "DispatchConversationalGuidanceControlAsync", "next");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await InvokeAsync(host, "DispatchConversationalGuidanceControlAsync", "next");
            Assert.Equal("NextAsync", Assert.Single(proxy.Calls));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData("next")]
    [InlineData("back")]
    [InlineData("skip")]
    public async Task DetachedStepCommandDoesNotResumePausedGuidance(string command)
    {
        var client = new FakeReviewClient
        {
            ResumeResult = new(EllaiClientOperationStatus.Accepted,
                CreateSession(1) with { Guidance = new("saved-proposal", "saved-plan", "report", "Paused", 4) })
        };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        await InvokeAsync(host, "DispatchConversationalGuidanceControlAsync", command);
        Assert.Equal(0, client.GuidancePlanCalls);
        Assert.Empty(client.GuidanceControls);
    }

    [Theory]
    [InlineData(BonboardingTourStatus.Paused)]
    [InlineData(BonboardingTourStatus.Completed)]
    [InlineData(BonboardingTourStatus.Cancelled)]
    public async Task ConversationalNextDoesNotAdvanceInactiveGuidance(BonboardingTourStatus status)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = status;
        SetField(host, "_generatedTour", tour);
        await InvokeAsync(host, "DispatchConversationalGuidanceControlAsync", "next");
        Assert.Empty(proxy.Calls);
    }

    [Fact]
    public async Task NewerTurnInvalidatesPendingGuidanceProposal()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        await InvokeAsync(host, "ObserveGuidanceProposal", GuidanceTurn(), false);
        await InvokeAsync(host, "ObserveGuidanceProposal",
            new EllaiConversationTurnDto("next-turn", 2, "Something else", null, "Queued", 0, null), false);
        Assert.Null(GetField<string?>(host, "_guidanceProposalId"));
        await InvokeAsync(host, "StartProposedGuidanceAsync");
        Assert.Equal(0, client.GuidancePlanCalls);
    }

    [Theory]
    [InlineData("Completed", "Saved answer")]
    [InlineData("TerminalFailure", "This request couldn't be completed.")]
    [InlineData("Cancelled", "Request ended.")]
    public async Task HistoryDialogRendersDurableTurnsAsEncodedChat(string outcome, string expected)
    {
        var client = new FakeReviewClient
        {
            SavedConversation = [new("saved", 1, "<script>user</script>", "Saved answer", outcome, 1, DateTimeOffset.UtcNow)],
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        services.AddSingleton<IEllaiReviewClient>(client);
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
            await renderer.RenderComponentAsync<MudBlazor.MudDialogProvider>());
        await renderer.Dispatcher.InvokeAsync(async () =>
            await provider.GetRequiredService<MudBlazor.IDialogService>().ShowAsync<EllaiConversationDialog>(
                "Conversation", new MudBlazor.DialogParameters { ["SessionId"] = "saved-session" }));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Equal("saved-session", client.HistorySessionId);
        Assert.Contains("&lt;script&gt;user&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>user</script>", html);
        Assert.Contains(expected.Replace("'", "&#x27;"), html);
        Assert.Contains("You", html);
        Assert.Contains("Ellai", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalAnswerPreemptsAcknowledgementAndWaitsForLocalStopWithoutOverlap(bool stopFails)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        var speech = new BlockingNarrationJsModule();
        SetField(host, "_speechModule", speech);
        SetField(host, "_readResponsesAloud", true);
        var ack = new EllaiSessionResponseDto(1, "session-1", "One moment.", DateTimeOffset.UtcNow, "u", IsFinal: false,
            Progress: new("u:initial", "Acknowledgement", "Checking the records", true, 1, DateTimeOffset.UtcNow.AddSeconds(8)));
        await InvokeAsync(host, "OnResponseReceived", ack);
        await InvokeAsync(host, "OnResponseReceived", ack);
        Assert.Equal(1, speech.Starts);
        Assert.Null(GetField<string?>(host, "_latestResponseText"));
        Assert.Equal("Checking the records", GetField<string?>(host, "_toolActivityText"));
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(2, "session-1", "Here is the answer.", DateTimeOffset.UtcNow, "u"));
        Assert.Equal(1, speech.Starts);
        Assert.True(speech.StopRequested);
        if (stopFails) speech.StopCompleted.TrySetException(new InvalidOperationException("Synthetic stop failure"));
        else speech.StopCompleted.TrySetResult();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (GetField<bool>(host, "_responseAudioQueueRunning") && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(stopFails ? 1 : 2, speech.Starts);
        Assert.Equal(1, speech.MaximumConcurrent);
        Assert.Equal("Here is the answer.", GetField<string?>(host, "_latestResponseText"));
        Assert.Null(GetField<string?>(host, "_toolActivityText"));
    }

    [Fact]
    public async Task FreshConversationClearsPriorTimelineAndAcceptsItsFirstTurn()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        await InvokeAsync(host, "OnConversationUpdated", (object)new EllaiConversationTurnDto[]
        {
            new("old", 4, "Old question", "Old answer", "Completed", 1, DateTimeOffset.UtcNow),
        });
        await InvokeAsync(host, "CancelPriorSession");
        Assert.Null(GetField<string?>(host, "_latestResponseText"));
        await InvokeAsync(host, "OnConversationUpdated", (object)new EllaiConversationTurnDto[]
        {
            new("fresh", 1, "Fresh question", "Fresh answer", "Completed", 1, DateTimeOffset.UtcNow),
        });
        Assert.Equal("Fresh answer", GetField<string?>(host, "_latestResponseText"));
    }

    [Fact]
    public async Task DurableAnswerIsNotAppendedAgainWhenItsLateChunksArrive()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(1, "session-1", "Earlier answer", DateTimeOffset.UtcNow, "old-turn"));
        await InvokeAsync(host, "OnConversationUpdated", (object)new EllaiConversationTurnDto[]
        {
            new("new-turn", 2, "Latest question", "Latest complete answer", "Completed", 1, DateTimeOffset.UtcNow),
        });
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(2, "session-1", "Latest complete answer", DateTimeOffset.UtcNow, "new-turn", false));
        Assert.Equal("Latest complete answer", GetField<string?>(host, "_latestResponseText"));
        Assert.Equal("Latest question", GetField<string?>(host, "_latestUserText"));
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(3, "session-1", "Delayed older answer", DateTimeOffset.UtcNow, "old-turn"));
        Assert.Equal("Latest complete answer", GetField<string?>(host, "_latestResponseText"));
    }

    [Fact]
    public async Task ComposerShowsLatestSentMessageAndCaptionSizedReplyBesideOneStatusRow()
    {
        var html = await RenderComposerAsync(responseText: "Latest reply", sentMessage: "Latest sent question");
        Assert.Contains("Latest sent question", html, StringComparison.Ordinal);
        Assert.Contains("Your latest message", html, StringComparison.Ordinal);
        Assert.Contains("ellai-composer__status-row", html, StringComparison.Ordinal);
        Assert.Matches("mud-typography-caption[^>]*ellai-composer__response", html);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task ListenToggleExposesItsAccessiblePressedState(bool enabled, string expected)
    {
        var html = await RenderComposerAsync(isListeningEnabled: enabled);
        Assert.Contains($"aria-pressed=\"{expected}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListenToggleReadsNewTypedRepliesUntilDisabledWithoutReplayingFinalReconciliation()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        var speech = new RecordingJsModule();
        SetField(host, "_speechModule", speech);
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(1, "session-1", "First answer.", DateTimeOffset.UtcNow, "first"));
        Assert.DoesNotContain("playResponse", speech.Invocations);
        await InvokeAsync(host, "ToggleResponseAudioAsync");
        Assert.Single(speech.Invocations, call => call == "playResponse");
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(2, "session-1", "Next answer.", DateTimeOffset.UtcNow, "next", IsFinal: false));
        Assert.Equal(2, speech.Invocations.Count(call => call == "playResponse"));
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(3, "session-1", "Next answer.", DateTimeOffset.UtcNow, "next",
                IsReconciliation: true, Outcome: "Completed"));
        Assert.Equal(2, speech.Invocations.Count(call => call == "playResponse"));
        await InvokeAsync(host, "ToggleResponseAudioAsync");
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(4, "session-1", "Silent answer.", DateTimeOffset.UtcNow, "silent"));
        Assert.Equal(2, speech.Invocations.Count(call => call == "playResponse"));
    }

    [Fact]
    public async Task SentencesArrivingDuringPlaybackAreAllReadOnceWithoutOverlap()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        var speech = new BlockingNarrationJsModule();
        SetField(host, "_speechModule", speech);
        SetField(host, "_readResponsesAloud", true);
        for (var index = 1; index <= 3; index++)
            await InvokeAsync(host, "OnResponseReceived", new EllaiSessionResponseDto(index,
                "session-1", $"Sentence {index}.", DateTimeOffset.UtcNow, "multi", IsFinal: false));
        await InvokeAsync(host, "OnResponseReceived", new EllaiSessionResponseDto(4, "session-1",
            "Sentence 1. Sentence 2. Sentence 3.", DateTimeOffset.UtcNow, "multi",
            IsReconciliation: true, Outcome: "Completed"));
        Assert.Equal(1, speech.Starts);
        speech.FinishFirstPlayback();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (GetField<bool>(host, "_responseAudioQueueRunning") && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(3, speech.Starts);
        Assert.Equal(1, speech.MaximumConcurrent);
        Assert.False(speech.StopRequested);
    }

    [Fact]
    public async Task RepeatedDurableProjectionDoesNotOverwriteActiveStepNarration()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        var turn = GuidanceTurn();
        await InvokeAsync(host, "OnConversationUpdated", (object)new[] { turn });
        SetField(host, "_latestResponseText", "Select the subject of your report.");
        await InvokeAsync(host, "OnConversationUpdated", (object)new[] { turn with { UiProposals = turn.UiProposals.ToArray() } });
        Assert.Equal("Select the subject of your report.", GetField<string?>(host, "_latestResponseText"));
    }

    [Fact]
    public async Task RetriedGuidanceCanConsumeItsAlreadyPolledEventOnce()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        SetField(host, "_generatedTour", DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>());
        var response = new EllaiSessionResponseDto(3, "session-1", "Enter the reported date.", DateTimeOffset.UtcNow,
            "turn", IsFinal: false, Progress: new("validation-event", "Guidance", "Guidance", true, 1, DateTimeOffset.UtcNow.AddMinutes(1)));

        await InvokeAsync(host, "OnResponseReceived", response);
        Assert.Null(GetField<string?>(host, "_latestResponseText"));
        SetField(host, "_guidanceNarrationEventId", "validation-event");
        await InvokeAsync(host, "OnResponseReceived", response);
        Assert.Equal(response.Text, GetField<string?>(host, "_latestResponseText"));
        Assert.Equal(3, GetField<long>(host, "_latestResponseSequence"));
        await InvokeAsync(host, "OnResponseReceived", response);
        Assert.Single(GetField<List<long>>(host, "_latestResponseAudioSequences"));
        await InvokeAsync(host, "OnResponseReceived", new EllaiSessionResponseDto(4, "session-1", "Newer answer", DateTimeOffset.UtcNow, "turn"));
        await InvokeAsync(host, "OnResponseReceived", response);
        Assert.Equal("Newer answer", GetField<string?>(host, "_latestResponseText"));
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    public async Task CurrentGuidanceUsesNormalComposerAndAudioAfterTheReasoningTurnSettles(bool voiceOn, bool current, int expectedPlayback)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        var speech = new BlockingNarrationJsModule();
        SetField(host, "_speechModule", speech);
        SetField(host, "_readResponsesAloud", voiceOn);
        SetField(host, "_settledUtteranceId", "turn");
        SetField(host, "_guidanceNarrationEventId", "current-step");
        SetField(host, "_generatedTour", DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>());
        var narration = new EllaiSessionResponseDto(1, "session-1", "Choose the report type.", DateTimeOffset.UtcNow,
            "turn", IsFinal: false, Progress: new(current ? "current-step" : "old-step", "Guidance", "Guidance", true, 1, DateTimeOffset.UtcNow.AddMinutes(1)));
        await InvokeAsync(host, "OnResponseReceived", narration);
        await InvokeAsync(host, "OnResponseReceived", narration);
        Assert.Equal(expectedPlayback, speech.Starts);
        Assert.Equal(current ? narration.Text : null, GetField<string?>(host, "_latestResponseText"));
        Assert.Null(GetField<string?>(host, "_toolActivityText"));
        speech.FinishFirstPlayback();
    }

    [Fact]
    public async Task VoiceEntryWaitsForAnActiveSessionAndRecoversAfterStartup()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        SetField(host, "_sessionView", null);
        SetField(host, "_startingSession", true);
        Assert.False(GetProperty<bool>(host, "CanStartVoiceCapture"));
        SetField(host, "_sessionView", CreateSession(version: 1));
        Assert.False(GetProperty<bool>(host, "CanStartVoiceCapture"));
        SetField(host, "_startingSession", false);
        Assert.True(GetProperty<bool>(host, "CanStartVoiceCapture"));
        SetField(host, "_startingVoiceCapture", true);
        Assert.False(GetProperty<bool>(host, "CanStartVoiceCapture"));
        SetField(host, "_startingVoiceCapture", false);
        SetField(host, "_isRecordingAudio", true);
        Assert.False(GetProperty<bool>(host, "CanStartVoiceCapture"));
    }

    [Fact]
    public void AudioProcessingStatusShowsEffectiveAndUnreportedCapabilities()
    {
        var status = new EllaiAudioProcessingStatus("iOS", true, null, null, true);

        Assert.Equal(
            "iOS audio processing: echo cancellation on, noise suppression not reported, automatic gain control not reported.",
            status.ToStatusText());
        Assert.False(EllaiAudioProcessingStatus.Unknown.CaptureActive);
        Assert.Contains("echo cancellation not reported", EllaiAudioProcessingStatus.Unknown.ToStatusText());
    }

    [Fact]
    public async Task PublicLayoutExposesOnlyGuestAskModeToUnauthenticatedVisitors()
    {
        var markup = await RenderPublicLayoutAsync();

        Assert.Contains("Public page", markup, StringComparison.Ordinal);
        Assert.Contains("Open Ellai actions", markup, StringComparison.Ordinal);
        Assert.Contains("Ask", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Guide Me", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Help Me Work", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposerKeepsSendDisabledWhenHostCannotSubmit()
    {
        var markup = await RenderComposerAsync();

        Assert.Contains("aria-label=\"Send to Ellai\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Ellai voice and text controls\"", markup, StringComparison.Ordinal);
        Assert.Contains("Message Ellai", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "For privacy, avoid sharing names, contact details, or other details that could identify someone.",
            markup,
            StringComparison.Ordinal);
        Assert.DoesNotContain("configured AI service", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start voice input", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop live voice", markup, StringComparison.Ordinal);
        Assert.Matches("""(?s)<button(?=[^>]*aria-label="Send to Ellai")(?=[^>]*disabled)[^>]*>""", markup);
    }

    [Fact]
    public async Task LivePartialTranscriptIsDisplayedSeparatelyFromTheEditableDraft()
    {
        var markup = await RenderComposerAsync(
            inputText: "existing draft",
            livePartialTranscript: "transient recognized words");

        Assert.Contains("class=\"ellai-composer__partial-transcript\" aria-live=\"off\"", markup,
            StringComparison.Ordinal);
        Assert.Contains("transient recognized words", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Heard so far:", markup, StringComparison.Ordinal);
        Assert.Contains("existing draft", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveCaptureKeepsPlaybackUntilGatewayConfirmsSpeech()
    {
        var statusReads = 0;
        var streamCompletion = new TaskCompletionSource<EllaiVoiceTranscriptionResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => streamCompletion.Task,
            VoiceTurnStatusHandler = (_, _) =>
            {
                var state = Interlocked.Increment(ref statusReads) == 1 ? "Listening" : "UserSpeaking";
                return Task.FromResult<EllaiVoiceTurnStatusDto?>(new(state, 1, 320, 0, null));
            },
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var captureModule = new RecordingJsModule();
        const string playbackId = "playback-current";
        SetField(host, "_captureModule", captureModule);
        SetField(host, "_microphoneCaptureAvailable", true);
        SetField(host, "_voiceNetworkOnline", true);
        SetField(host, "_isPlayingResponse", true);
        SetField(host, "_activeResponseSequence", 7L);
        SetField(host, "_activeResponsePlaybackId", playbackId);

        await InvokeAsync(host, "StartLiveCaptureAsync");

        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.True(GetField<bool>(host, "_isPlayingResponse"),
            $"Response playback was cancelled. Status: {GetField<string?>(host, "StatusText")}");
        Assert.Equal(7L, GetField<long>(host, "_activeResponseSequence"));
        Assert.Equal(playbackId, GetField<string?>(host, "_activeResponsePlaybackId"));
        Assert.Equal("Ready", GetField<string?>(host, "StatusText"));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (GetField<bool>(host, "_isPlayingResponse") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.False(GetField<bool>(host, "_isPlayingResponse"));
        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.Equal("Ready", GetField<string?>(host, "StatusText"));

        streamCompletion.SetResult(null);
        await InvokeAsync(host, "StopAndDiscardVoiceCaptureAsync");
    }

    [Fact]
    public async Task GreetingStartsAfterCaptureAndDoesNotEndIt()
    {
        var streaming = new TaskCompletionSource<EllaiVoiceTranscriptionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient { LiveTranscriptionHandler = (_, _) => streaming.Task };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var module = new RecordingJsModule();
        SetField(host, "_captureModule", module);
        SetField(host, "_speechModule", module);
        SetField(host, "_microphoneCaptureAvailable", true);
        SetField(host, "_voiceNetworkOnline", true);
        try
        {
            await InvokeAsync(host, "StartLiveCaptureAsync");
            Assert.True(module.Invocations.IndexOf("startCapture") < module.Invocations.IndexOf("playListeningGreeting"));
            Assert.True(GetField<bool>(host, "_isRecordingAudio"));
            Assert.True(GetField<bool>(host, "_readResponsesAloud"));
            Assert.DoesNotContain("stopCapture", module.Invocations);
        }
        finally { streaming.TrySetResult(null); }
    }

    [Fact]
    public async Task ComposerStatusPrioritizesConnectionRecoveryOverThinking()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(host);
        var statusMethod = typeof(EllaiGuidanceHost).GetMethod(
            "GetComposerStatusText", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task<string?> GetStatus() => await testHost.Dispatcher.InvokeAsync(
            () => (string?)statusMethod.Invoke(testHost.Component, null));

        Assert.Equal("Ready", await GetStatus());
        SetField(host, "_thinkingUtteranceId", "pending-turn");
        Assert.Equal("Thinking…", await GetStatus());
        SetField(host, "_startingSession", true);
        Assert.Equal("Connecting…", await GetStatus());
        SetField(host, "_startingSession", false);
        SetField(host, "_liveVoiceReconnectRequired", true);
        Assert.Equal("Reconnecting…", await GetStatus());
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("TerminalFailure")]
    [InlineData("Cancelled")]
    public async Task ThinkingTracksAcceptedTurnUntilItsDurableOutcome(string outcome)
    {
        var client = new FakeReviewClient(new EllaiUtteranceSubmitResult(
            EllaiClientOperationStatus.Accepted, CreateSession(version: 2)));
        using var host = await CreateHost(client, CreateSession(version: 1), "Synthetic question");
        await InvokeAsync(host, "OnSend");
        var id = Assert.Single(client.Submissions).ClientUtteranceId;
        Assert.Equal("Synthetic question", GetField<string?>(host, "_latestUserText"));
        Assert.Equal(id, GetField<string?>(host, "_thinkingUtteranceId"));
        await InvokeAsync(host, "OnConversationUpdated", (object)new EllaiConversationTurnDto[]
        {
            new("older-turn", 1, "Earlier question", "Earlier answer", "Completed", 1, DateTimeOffset.UtcNow),
        });
        Assert.Equal(id, GetField<string?>(host, "_thinkingUtteranceId"));
        await InvokeAsync(host, "OnConversationUpdated", (object)new EllaiConversationTurnDto[]
        {
            new(id, 2, "Synthetic question", outcome == "Completed" ? "Answer" : null, outcome, 1, DateTimeOffset.UtcNow),
        });
        Assert.Null(GetField<string?>(host, "_thinkingUtteranceId"));
    }

    [Fact]
    public async Task ThinkingRestoresPendingHistoryAndClearsWhenAdmissionFails()
    {
        var client = new FakeReviewClient(new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.InvalidRequest, null));
        using var host = await CreateHost(client, CreateSession(version: 1), "Synthetic question");
        await InvokeAsync(host, "OnSend");
        Assert.Null(GetField<string?>(host, "_thinkingUtteranceId"));
        await InvokeAsync(host, "OnConversationUpdated", (object)new EllaiConversationTurnDto[]
        {
            new("restored-turn", 2, "Earlier question", null, "Processing", 1, null),
        });
        Assert.Equal("restored-turn", GetField<string?>(host, "_thinkingUtteranceId"));
    }

    [Fact]
    public async Task RetryUsesOnlyTheAnimatedStatusAndReturnsToThinkingOnWorkerPickup()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(host);
        var status = typeof(EllaiGuidanceHost).GetMethod("GetComposerStatusText", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var outcome in new[] { "RetryableFailure", "Processing" })
        {
            await InvokeAsync(host, "OnConversationUpdated", (object)new EllaiConversationTurnDto[]
            {
                new("retry-turn", 1, "Synthetic question", null, outcome, 1, DateTimeOffset.UtcNow),
            });
            Assert.Null(GetField<string?>(host, "_latestResponseText"));
            Assert.Equal(outcome == "RetryableFailure" ? "Reconnecting…" : "Thinking…",
                await testHost.Dispatcher.InvokeAsync(() => (string?)status.Invoke(testHost.Component, null)));
        }
    }

    [Fact]
    public async Task ConfirmedGatewaySpeechStopsNativeResponsePlayback()
    {
        var client = new FakeReviewClient
        {
            VoiceTurnStatusHandler = (_, _) => Task.FromResult<EllaiVoiceTurnStatusDto?>(
                new("UserSpeaking", 1, 320, 0, null)),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isPlayingResponse", true);
        SetField(host, "_activeResponseSequence", 7L);
        SetField(host, "_activeResponsePlaybackId", "playback-current");

        await InvokeAsync(host, "StartVoiceTurnStatusPollingAsync", "session-1");

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (GetField<bool>(host, "_isPlayingResponse") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await InvokeAsync(host, "StopVoiceTurnStatusPollingAsync");

        Assert.True(client.VoiceTurnStatusReads > 0);
        Assert.False(GetField<bool>(host, "_isPlayingResponse"));
        Assert.Equal("Ready", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task InterruptedLiveStreamSubmitsOnlyTheGatewayConfirmedFinalTurn()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 2)))
        {
            LiveTranscriptionHandler = (_, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("recovered words")),
            VoiceTurnStatusHandler = (_, _) => Task.FromResult<EllaiVoiceTurnStatusDto?>(
                CompletedVoiceTurnStatus("recovered words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");

        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 1234, -2345 });
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        Assert.Empty(client.VoiceTranscriptions);
        var submitted = Assert.Single(client.Submissions);
        Assert.Equal("recovered words", submitted.FinalizedText);
    }

    [Fact]
    public async Task RecordedVoiceTranscriptUsesTheSameAutomaticSubmissionFlowAsLiveVoice()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 2)))
        {
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("Can I submit a report without an account?")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        SetField(host, "_capturedPcm", new MemoryStream([1, 2, 3, 4]));
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Recorded);
        SetField(host, "_isRecordingAudio", true);

        await InvokeAsync(host, "StopVoiceCaptureAsync");

        Assert.Equal(EllaiVoiceCaptureMode.Recorded, Assert.Single(client.VoiceTranscriptions).Mode);
        var submitted = Assert.Single(client.Submissions);
        Assert.Equal("Can I submit a report without an account?", submitted.FinalizedText);
        Assert.Null(GetField<string?>(host, "InputText"));
        Assert.Contains("Your message was accepted", GetField<string?>(host, "StatusText"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedGatewayTurnAutoSubmitsAndKeepsTheMicrophoneSessionOpen()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 2)))
        {
            VoiceTurnStatusHandler = (_, _) => Task.FromResult<EllaiVoiceTurnStatusDto?>(new(
                "TurnCompleted", 1, 1_200, 800, null,
                [new EllaiCompletedVoiceTurnDto(1, "muse-turn-1", "How do I follow up on a report?")])),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_voiceNetworkOnline", true);
        await InvokeAsync(host, "StartVoiceTurnStatusPollingAsync", "session-1");

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (client.Submissions.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal("How do I follow up on a report?", Assert.Single(client.Submissions).FinalizedText);
        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.Equal(EllaiVoiceCaptureMode.Live, GetField<EllaiVoiceCaptureMode?>(host, "_captureMode"));
        await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync",
            new EllaiVoiceTurnStatusDto("TurnCompleted", 1, 1_200, 800, null,
                [new EllaiCompletedVoiceTurnDto(1, "muse-turn-1", "How do I follow up on a report?")]),
            "session-1", false);
        Assert.Single(client.Submissions);
        await InvokeAsync(host, "StopVoiceTurnStatusPollingAsync");
    }

    [Fact]
    public async Task ServerAdmittedVoiceTurnUpdatesComposerWithoutResubmittingOrStoppingCapture()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(version: 3), "unfinished typed draft");
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        var turnId = Guid.NewGuid().ToString("N");
        var status = new EllaiVoiceTurnStatusDto("TurnCompleted", 1, 900, 400, null,
            [new(1, "muse-1", "Show my assigned cases", "session-1", turnId, 1,
                ServerSubmission: true, Admission: new(EllaiClientOperationStatus.Accepted, CreateSession(version: 2)))]);

        await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync", status, "session-1", false);
        await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync", status, "session-1", false);

        Assert.Empty(client.Submissions);
        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.Equal("Show my assigned cases", GetField<string?>(host, "_latestUserText"));
        Assert.Equal("unfinished typed draft", GetField<string?>(host, "InputText"));
        Assert.Equal(turnId, GetField<string?>(host, "_thinkingUtteranceId"));
        Assert.Equal(3, GetField<EllaiSessionView>(host, "_sessionView").SessionVersion);
    }

    [Fact]
    public async Task VoiceNotificationAfterDurableAnswerDoesNotRestartThinkingOrClearAnswer()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(version: 2), string.Empty);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        var turnId = Guid.NewGuid().ToString("N");
        await InvokeAsync(host, "OnConversationUpdated", (object)new EllaiConversationTurnDto[]
        {
            new(turnId, 1, "Hello", "Hello back", "Completed", 1, DateTimeOffset.UtcNow),
        });
        await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync",
            new EllaiVoiceTurnStatusDto("TurnCompleted", 1, 900, 400, null,
                [new(1, "muse-1", "Hello", "session-1", turnId, 1, ServerSubmission: true,
                    Admission: new(EllaiClientOperationStatus.Accepted, CreateSession(version: 2)))]),
            "session-1", false);

        Assert.Empty(client.Submissions);
        Assert.Null(GetField<string?>(host, "_thinkingUtteranceId"));
        Assert.Equal("Hello back", GetField<string?>(host, "_latestResponseText"));
    }

    [Fact]
    public async Task UnavailableVoiceAdmissionRetainsStableIdentityForManualRetry()
    {
        var client = new FakeReviewClient(new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Accepted, CreateSession(version: 2)));
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        var turnId = Guid.NewGuid().ToString("N");
        await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync",
            new EllaiVoiceTurnStatusDto("TurnCompleted", 1, 900, 400, null,
                [new(1, "muse-1", "Hello", "session-1", turnId, 1, ServerSubmission: true,
                    Admission: new(EllaiClientOperationStatus.Unavailable, null))]), "session-1", false);
        Assert.Empty(client.Submissions);
        Assert.Equal("Hello", GetField<string?>(host, "InputText"));
        Assert.Null(GetField<string?>(host, "_thinkingUtteranceId"));
        await InvokeAsync(host, "OnSend");
        Assert.Equal(turnId, Assert.Single(client.Submissions).ClientUtteranceId);
    }

    [Fact]
    public async Task EndpointCandidateWithoutCompletedTurnDoesNotSubmitOrStopCapture()
    {
        var client = new FakeReviewClient
        {
            VoiceTurnStatusHandler = (_, _) => Task.FromResult<EllaiVoiceTurnStatusDto?>(
                new("EndpointCandidate", 1, 1_200, 800, "unfinished words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);

        await InvokeAsync(host, "StartVoiceTurnStatusPollingAsync", "session-1");
        await Task.Delay(50);
        await InvokeAsync(host, "StopVoiceTurnStatusPollingAsync");

        Assert.Empty(client.Submissions);
        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
    }

    [Fact]
    public async Task ReconnectedProviderEpochSubmitsTheNextSessionSequenceOnceWithItsStableTurnId()
    {
        var turnId = Guid.NewGuid();
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 2)));
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_voiceTurnCursorSessionId", "session-1");
        SetField(host, "_lastCompletedVoiceTurnSequence", 4L);
        SetField(host, "_lastVoiceTurnStatusEpoch", 1L);

        var status = new EllaiVoiceTurnStatusDto(
            "TurnCompleted", 2, 900, 700, null,
            [new EllaiCompletedVoiceTurnDto(
                5, "muse-turn-1", "Actually, show only urgent cases.",
                "session-1", turnId.ToString("N"), 2)],
            "session-1");

        await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync", status, "session-1", false);
        await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync", status, "session-1", false);

        var submitted = Assert.Single(client.Submissions);
        Assert.Equal("Actually, show only urgent cases.", submitted.FinalizedText);
        Assert.Equal(turnId.ToString("N"), submitted.ClientUtteranceId);
        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.Equal(5L, GetField<long>(host, "_lastCompletedVoiceTurnSequence"));
    }

    [Fact]
    public async Task FailedLiveStreamRecoveryExplainsThatTheTurnWasNotSent()
    {
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTranscriptionHandler = (_, _, _, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            // Supply authoritative recovery status so the fixture can evaluate the final turn outcome.
            VoiceTurnStatusHandler = (_, _) => Task.FromResult<EllaiVoiceTurnStatusDto?>(new("Recovering", 1, 0, 0, null, [], "session-1")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");

        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 100 });
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        Assert.Single(client.VoiceTranscriptions);
        Assert.Contains("Couldn't send your voice message", GetField<string?>(host, "StatusText"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task BrowserChannelBackpressureSwitchesToBoundedPcmRecovery()
    {
        var streamCompletion = new TaskCompletionSource<EllaiVoiceTranscriptionResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => streamCompletion.Task,
            VoiceTranscriptionHandler = (_, _, _, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");

        for (var sample = 0; sample < 9; sample++)
        {
            await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { sample + 1 });
        }

        Assert.True(GetField<bool>(host, "_liveVoiceReconnectRequired"));
        streamCompletion.SetResult(null);
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var recovery = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(18, recovery.Audio.Length);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task BrowserBackpressureKeepsCaptureUntilExplicitStop()
    {
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("buffered words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        var pcm = new MemoryStream([1, 2, 3, 4]);
        SetField(host, "_capturedPcm", pcm);
        SetField(host, "_capturedVoiceBytes", 4L);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");

        await InvokeAsync(host, "OnVoiceCaptureBackpressure");

        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.Empty(client.VoiceTranscriptions);
        await InvokeAsync(host, "StopVoiceCaptureAsync");
        Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, client.VoiceTranscriptions[0].Audio);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task ConnectivityRestorationReplaysBufferedAudioAndResumesLiveCapture()
    {
        var call = 0;
        var streams = new List<byte[]>();
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = async (_, stream) =>
            {
                var callNumber = Interlocked.Increment(ref call);
                using var replayed = new MemoryStream();
                await stream.CopyToAsync(replayed);
                streams.Add(replayed.ToArray());
                if (callNumber == 1)
                {
                    return null;
                }

                return new EllaiVoiceTranscriptionResult("resumed words");
            },
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_voiceNetworkOnline", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");
        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 10, 11 });

        await InvokeAsync(host, "OnVoiceConnectivityChanged", false);
        await InvokeAsync(host, "OnVoiceConnectivityChanged", true);
        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 12 });

        Assert.False(GetField<bool>(host, "_liveVoiceReconnectRequired"));
        Assert.True(GetField<bool>(host, "_voiceNetworkOnline"));
        Assert.Equal("Ready", GetField<string?>(host, "StatusText"));
        Assert.Equal(2, client.LiveTranscriptionCalls);

        await InvokeAsync(host, "StopVoiceCaptureAsync");

        Assert.Empty(client.VoiceTranscriptions);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
        Assert.Equal(2, client.LiveTranscriptionCalls);
        var completedStream = Assert.Single(streams.Skip(1));
        Assert.Equal(new byte[] { 10, 0, 11, 0, 12, 0 }, completedStream[..6]);
        // Segmented capture forwards real samples; the client must not synthesize trailing silence.
        Assert.Equal(6, completedStream.Length);
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task FailedLiveReconnectFallsBackToOneBufferedUpload()
    {
        var call = 0;
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = async (_, stream) =>
            {
                Interlocked.Increment(ref call);
                await stream.CopyToAsync(Stream.Null);
                return null;
            },
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("uploaded words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_voiceNetworkOnline", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");
        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 21, 22 });

        await InvokeAsync(host, "OnVoiceConnectivityChanged", false);
        await InvokeAsync(host, "OnVoiceConnectivityChanged", true);
        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 23 });
        // Explicitly simulate the failed reconnect signal. A null final reply alone is not a transport failure.
        SetField(host, "_liveVoiceReconnectRequired", true);
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var upload = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(new byte[] { 21, 0, 22, 0, 23, 0 }, upload.Audio);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
        Assert.Equal(2, client.LiveTranscriptionCalls);
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task LiveRecoveryUploadsOnlyTheTailAfterTheLatestCompletedSampleSpan()
    {
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTranscriptionHandler = (_, _, _, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTurnStatusHandler = (_, _) => Task.FromResult<EllaiVoiceTurnStatusDto?>(new(
                "TurnCompleted", 2, 1_200, 700, null,
                [new EllaiCompletedVoiceTurnDto(
                    1, "muse-1", "Already submitted.", "session-1", Guid.NewGuid().ToString("N"), 1,
                    AudioStartSample: 0, AudioEndSample: 2_000, VoiceCaptureId: "capture-current")],
                "session-1")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        var capturedPcm = Enumerable.Range(0, 5_000).Select(value => (byte)(value % 251)).ToArray();
        SetField(host, "_capturedPcm", new MemoryStream(capturedPcm));
        SetField(host, "_capturedVoiceBytes", (long)capturedPcm.Length);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_lastCompletedVoiceTurnSequence", 1L);
        SetField(host, "_liveVoiceCaptureId", "capture-current");
        SetField(host, "_voiceNetworkOnline", false);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");
        SetField(host, "_liveVoiceReconnectRequired", true);

        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var recovery = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(capturedPcm[4_000..], recovery.Audio);
        Assert.Equal("capture-current", recovery.VoiceCaptureId);
        Assert.Equal(2_000, recovery.VoiceCaptureOffsetSamples);
        Assert.Empty(client.Submissions);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
    }

    [Fact]
    public async Task NewMicrophoneCaptureDoesNotReuseOffsetsFromAnEarlierCapture()
    {
        const string newCaptureId = "capture-new";
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTranscriptionHandler = (_, _, _, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTurnStatusHandler = (_, _) => Task.FromResult<EllaiVoiceTurnStatusDto?>(new(
                "TurnCompleted", 2, 1_200, 700, null,
                [new EllaiCompletedVoiceTurnDto(
                    1, "muse-old", "Earlier capture.", "session-1", Guid.NewGuid().ToString("N"), 1,
                    AudioStartSample: 0, AudioEndSample: 2_000, VoiceCaptureId: "capture-old")],
                "session-1")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        var capturedPcm = new byte[] { 3, 4, 5, 6, 7, 8 };
        SetField(host, "_capturedPcm", new MemoryStream(capturedPcm));
        SetField(host, "_capturedVoiceBytes", (long)capturedPcm.Length);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_lastCompletedVoiceTurnSequence", 1L);
        SetField(host, "_liveVoiceCaptureId", newCaptureId);
        SetField(host, "_voiceNetworkOnline", false);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");
        SetField(host, "_liveVoiceReconnectRequired", true);

        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var recovery = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(capturedPcm, recovery.Audio);
        Assert.Equal(newCaptureId, recovery.VoiceCaptureId);
        Assert.Equal(0, recovery.VoiceCaptureOffsetSamples);
    }

    [Fact]
    public async Task NativeCaptureBuffersPcmWithoutStartingLiveStreamWhenOffline()
    {
        var client = new FakeReviewClient
        {
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("offline words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_liveVoiceReconnectRequired", true);
        SetField(host, "_voiceNetworkOnline", false);
        var queueMethod = typeof(EllaiGuidanceHost).GetMethod(
            "QueueNativePcmChunkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var chunk = new ReadOnlyMemory<byte>(new byte[] { 8, 9, 10, 11 });

        await (ValueTask)queueMethod.Invoke(host.Component, [chunk, CancellationToken.None])!;
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var recovery = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(new byte[] { 8, 9, 10, 11 }, recovery.Audio);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
        Assert.Empty(client.Submissions);
        Assert.Equal(0, client.LiveTranscriptionCalls);
    }

    [Fact]
    public async Task EmptyComposerShowsLiveVoiceControlInsteadOfSend()
    {
        var markup = await RenderComposerAsync(inputText: string.Empty, canUseMicrophone: true,
            onStartRealtimeVoice: EventCallback.Factory.Create(new object(), () => Task.CompletedTask),
            isLiveSpeaking: false);

        Assert.Contains("aria-label=\"Start live voice question\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Send to Ellai\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyComposerKeepsUnavailableVoiceControlVisibleAndDisabled()
    {
        var markup = await RenderComposerAsync(inputText: string.Empty, canUseMicrophone: false,
            isLiveSpeaking: false);

        Assert.Contains("aria-label=\"Live voice question unavailable\"", markup, StringComparison.Ordinal);
        Assert.Matches("""(?s)<button(?=[^>]*aria-label="Live voice question unavailable")(?=[^>]*disabled)[^>]*>""", markup);
        Assert.DoesNotContain("aria-label=\"Send to Ellai\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeLiveCaptureCanBeAvailableWhenBrowserRecordingIsUnavailable()
    {
        var callback = EventCallback.Factory.Create(new object(), () => Task.CompletedTask);
        var markup = await RenderComposerAsync(inputText: string.Empty,
            canUseMicrophone: false,
            canUseRealtimeMicrophone: true,
            onStartVoice: callback,
            onStartRealtimeVoice: callback,
            isLiveSpeaking: false);

        Assert.Matches("""(?s)<button(?=[^>]*aria-label="Voice recording unavailable")(?=[^>]*disabled)[^>]*>""", markup);
        Assert.Contains("aria-label=\"Start live voice question\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Live voice question unavailable\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposerGroupsVoiceControlsSettingsEndSessionAndClose()
    {
        var callback = EventCallback.Factory.Create(new object(), () => Task.CompletedTask);
        var markup = await RenderComposerAsync(
            inputText: string.Empty,
            canUseMicrophone: true,
            onStartVoice: callback,
            onStartRealtimeVoice: callback,
            onOpenSettings: callback,
            isLiveSpeaking: false);

        var fieldActionsStart = markup.IndexOf("ellai-composer__field-actions", StringComparison.Ordinal);
        var microphoneIndex = markup.IndexOf("Record a voice question", fieldActionsStart, StringComparison.Ordinal);
        var liveVoiceIndex = markup.IndexOf("Start live voice question", fieldActionsStart, StringComparison.Ordinal);
        Assert.True(microphoneIndex > fieldActionsStart);
        Assert.True(liveVoiceIndex > microphoneIndex);

        var headerActionsStart = markup.IndexOf("ellai-composer__header-actions", StringComparison.Ordinal);
        var headerActionsEnd = markup.IndexOf("ellai-composer__input-row", headerActionsStart, StringComparison.Ordinal);
        var headerActions = markup[headerActionsStart..headerActionsEnd];
        Assert.Contains("Ellai About and settings", headerActions, StringComparison.Ordinal);
        Assert.Contains("End Ellai session", headerActions, StringComparison.Ordinal);
        Assert.Contains("Close Ellai composer", headerActions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingComposerHidesItButKeepsActiveSessionAndDoesNotCancelIt()
    {
        var client = new FakeReviewClient();
        var session = CreateSession(version: 1);
        using var host = await CreateHost(client, session, string.Empty);
        SetField(host, "ShowComposer", true);

        await InvokeAsync(host, "CloseComposerAsync");

        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.Same(session, GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.Empty(client.Cancellations);
        Assert.Contains("still active", GetField<string?>(host, "StatusText"), StringComparison.Ordinal);

        await InvokeAsync(host, "SelectModeAsync", EllaiMode.Ask);

        Assert.True(GetField<bool>(host, "ShowComposer"));
        Assert.Same(session, GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.Equal(0, client.SessionStartCalls);
    }

    [Fact]
    public async Task ApprovalOutcomeWithoutConfirmingReceiptStaysUnknown()
    {
        var client = new FakeReviewClient
        {
            ListedReceipts = null,
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var review = new EllaiReviewDto(
            "review-1", "session-1", "plan-hash", 1, "Pending",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            [new EllaiApprovalActionDescriptor("action-1", "Open case", "Open case details")]);

        await InvokeAsync(host, "SubmitSelectionAsync", review, (IReadOnlyList<string>)["action-1"]);

        Assert.Contains("outcome unknown", GetField<string?>(host, "StatusText"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.ApprovalCalls);
        Assert.Equal(1, client.ReceiptListCalls);
    }

    [Fact]
    public async Task RejectionFailureStaysUnknownInsteadOfClaimingRejected()
    {
        var client = new FakeReviewClient
        {
            ActiveReview = new EllaiReviewDto(
                "review-1", "session-1", "plan-hash", 1, "Pending",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
                [new EllaiApprovalActionDescriptor("action-1", "Open case", "Open case details")]),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var review = client.ActiveReview;

        await InvokeAsync(host, "RejectAsync", review);

        Assert.Contains("Rejection outcome unknown", GetField<string?>(host, "StatusText"));
        Assert.NotEqual("Rejected.", GetField<string?>(host, "StatusText"));
        Assert.Equal(EllaiStatusPhases.Blocked, GetField<string?>(host, "Phase"));
        Assert.Equal(1, client.RejectionCalls);
        Assert.Equal(1, client.ActiveReviewReads);
    }

    [Fact]
    public async Task ConfirmedRejectionClosesReviewAndReportsSuccess()
    {
        var client = new FakeReviewClient
        {
            RejectionResult = true,
            ActiveReview = new EllaiReviewDto(
                "review-1", "session-1", "plan-hash", 1, "Pending",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
                [new EllaiApprovalActionDescriptor("action-1", "Open case", "Open case details")]),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var review = client.ActiveReview;

        await InvokeAsync(host, "RejectAsync", review);

        Assert.Equal("Review rejected.", GetField<string?>(host, "StatusText"));
        Assert.Equal(EllaiStatusPhases.Ready, GetField<string?>(host, "Phase"));
        Assert.Null(GetField<object?>(host, "_reviewDialog"));
        Assert.Equal(1, client.RejectionCalls);
        Assert.Equal(0, client.ActiveReviewReads);
    }

    [Fact]
    public async Task RejectionTransportFailureDoesNotClaimSuccess()
    {
        var client = new FakeReviewClient
        {
            RejectionException = new HttpRequestException("response unavailable"),
            ActiveReview = new EllaiReviewDto(
                "review-1", "session-1", "plan-hash", 1, "Pending",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
                [new EllaiApprovalActionDescriptor("action-1", "Open case", "Open case details")]),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);

        await InvokeAsync(host, "RejectAsync", client.ActiveReview);

        Assert.Contains("Rejection outcome unknown", GetField<string?>(host, "StatusText"));
        Assert.NotEqual("Review rejected.", GetField<string?>(host, "StatusText"));
        Assert.Equal(EllaiStatusPhases.Blocked, GetField<string?>(host, "Phase"));
        Assert.Equal(1, client.ActiveReviewReads);
    }

    [Fact]
    public async Task AmbiguousStartDoesNotOfferAnotherStartOrComposer()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), "");
        SetField(host, "_sessionView", null);

        await InvokeAsync(host, "SelectModeAsync", EllaiMode.Ask);

        Assert.True(GetField<bool>(host, "_startOutcomeUnknown"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.False(GetProperty<bool>(host, "CanSubmitText"));
        Assert.Contains("Do not start another session", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task ConfirmedAccessDenialDoesNotClaimRemoteSessionIsUnknown()
    {
        var client = new FakeReviewClient
        {
            StartHandler = (_, _) => Task.FromResult(
                new EllaiSessionStartResult(EllaiClientOperationStatus.PolicyBlocked)),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "");
        SetField(host, "_sessionView", null);

        await InvokeAsync(host, "SelectModeAsync", EllaiMode.Ask);

        Assert.False(GetField<bool>(host, "_startOutcomeUnknown"));
        Assert.True(GetField<bool>(host, "_showStartFailure"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.Contains("unavailable for this account or workspace", GetField<string?>(host, "StatusText"));
        Assert.DoesNotContain("contact support", GetField<string?>(host, "StatusText"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerCancellationEventUsesCommonTerminalCleanup()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), "draft");
        SetField(host, "ShowComposer", true);
        SetField(host, "IsActive", true);

        await InvokeAsync(host, "OnSessionCancelled");

        Assert.Null(GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.False(GetField<bool>(host, "IsActive"));
        Assert.Null(GetField<string?>(host, "InputText"));
        Assert.Equal("Ellai session ended.", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task LiveVoiceResponseIsShownAndAutomaticallyPlayedOnce()
    {
        NoOpJsModule.Invocations.Clear();
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_readResponsesAloud", true);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);

        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(1, "other-session", "Foreign response.", DateTimeOffset.UtcNow));
        Assert.NotEqual("Foreign response.", GetField<string?>(host, "StatusText"));

        const string currentResponse = "Here is the current result.";
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(1, "session-1", currentResponse, DateTimeOffset.UtcNow));
        await host.Dispatcher.InvokeAsync(async () => await Task.Yield());

        Assert.Equal(currentResponse, GetField<string?>(host, "_latestResponseText"));
        Assert.Equal(EllaiStatusPhases.Ready, GetField<string?>(host, "Phase"));
        Assert.Contains("playResponse", NoOpJsModule.Invocations);

        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(1, "session-1", "Duplicate response.", DateTimeOffset.UtcNow));
        await host.Dispatcher.InvokeAsync(async () => await Task.Yield());

        Assert.Equal(currentResponse, GetField<string?>(host, "_latestResponseText"));
        Assert.Equal(1, NoOpJsModule.Invocations.Count(invocation => invocation == "playResponse"));
    }

    [Fact]
    public async Task CompactComposerDisplaysResponseTextWhileSpeechPlays()
    {
        const string responseText = "Your assigned cases are listed here.";

        var html = await RenderComposerAsync(inputText: null, responseText: responseText);

        Assert.Contains(responseText, html, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalOutcomeReplacesProvisionalTextAndRejectsFailedCompletion()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), "draft");
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(1, "session-1", "Provisional answer", DateTimeOffset.UtcNow, "u-1", false));
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(2, "session-1", "Validated answer", DateTimeOffset.UtcNow, "u-1", true, true, "Completed"));
        await host.Dispatcher.InvokeAsync(async () => await Task.Yield());
        Assert.Equal("Validated answer", GetField<string?>(host, "_latestResponseText"));
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(3, "session-1", "Unsafe provisional", DateTimeOffset.UtcNow, "u-2", false));
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(4, "session-1", "", DateTimeOffset.UtcNow, "u-2", true, true, "TerminalFailure"));
        await host.Dispatcher.InvokeAsync(async () => await Task.Yield());
        Assert.DoesNotContain("Unsafe provisional", GetField<string?>(host, "_latestResponseText") ?? "");
        Assert.False(GetProperty<bool>(host, "CanPlayResponse"));
    }

    [Fact]
    public async Task UnavailableSubmitRetriesWithSameIdAndAcceptedNewSendGetsNewId()
    {
        var session = CreateSession(version: 1);
        var updatedSession = CreateSession(version: 2);
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Unavailable, null),
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Accepted, updatedSession),
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 3)));
        using var host = await CreateHost(client, session, "  first message  ");

        await InvokeAsync(host, "OnSend");
        await InvokeAsync(host, "OnSend");
        SetField(host, "InputText", "second message");
        await InvokeAsync(host, "OnSend");

        Assert.Equal(3, client.Submissions.Count);
        Assert.Equal(client.Submissions[0].ClientUtteranceId, client.Submissions[1].ClientUtteranceId);
        Assert.NotEqual(client.Submissions[1].ClientUtteranceId, client.Submissions[2].ClientUtteranceId);
        Assert.Equal("first message", client.Submissions[0].FinalizedText);
        Assert.Equal("typed", client.Submissions[0].InputSource);
        Assert.Equal(1, client.Submissions[1].ExpectedSessionVersion);
        Assert.Equal(2, client.Submissions[2].ExpectedSessionVersion);
    }

    [Fact]
    public async Task PolicyBlockedSubmissionKeepsTextAndAllowsCorrectedRetry()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.PolicyBlocked, null));
        using var host = await CreateHost(client, CreateSession(version: 1), "private report text");

        await InvokeAsync(host, "OnSend");

        Assert.Equal("private report text", GetField<string?>(host, "InputText"));
        Assert.True(GetProperty<bool>(host, "CanSubmitText"));
        Assert.Contains("current session policy", GetField<string?>(host, "StatusText"));
        Assert.Single(client.Submissions);
    }

    [Fact]
    public async Task SubmitConflictRebasesVersionBeforeStartingANewAttempt()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Conflict, CreateSession(version: 2)),
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 3)));
        using var host = await CreateHost(client, CreateSession(version: 1), "preserved question");

        await InvokeAsync(host, "OnSend");

        Assert.Equal("preserved question", GetField<string?>(host, "InputText"));
        Assert.Equal(2, GetField<EllaiSessionView?>(host, "_sessionView")?.SessionVersion);
        Assert.True(GetProperty<bool>(host, "CanSubmitText"));
        Assert.Contains("Review it, then send again", GetField<string?>(host, "StatusText"));

        await InvokeAsync(host, "OnSend");

        Assert.NotEqual(client.Submissions[0].ClientUtteranceId, client.Submissions[1].ClientUtteranceId);
        Assert.Equal(2, client.Submissions[1].ExpectedSessionVersion);
        Assert.Equal("preserved question", client.Submissions[1].FinalizedText);
    }

    [Fact]
    public async Task SubmitConflictWithoutCurrentViewKeepsTextAndBlocksResend()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Conflict, null));
        using var host = await CreateHost(client, CreateSession(version: 1), "preserved question");

        await InvokeAsync(host, "OnSend");

        Assert.Equal("preserved question", GetField<string?>(host, "InputText"));
        Assert.False(GetProperty<bool>(host, "CanSubmitText"));
        Assert.Equal(1, GetField<EllaiSessionView?>(host, "_sessionView")?.SessionVersion);
        Assert.Contains("remote state may still be active", GetField<string?>(host, "StatusText"));
        Assert.Single(client.Submissions);
    }

    [Fact]
    public async Task CancelConflictRefreshesVersionAndOnlyConfirmedCancelEndsSession()
    {
        var first = CreateSession(version: 1);
        var refreshed = CreateSession(version: 2);
        var client = new FakeReviewClient()
        {
            CancelResults =
                new Queue<EllaiSessionCancelResult>(new[]
                {
                    new EllaiSessionCancelResult(EllaiClientOperationStatus.Conflict, refreshed),
                    new EllaiSessionCancelResult(EllaiClientOperationStatus.Cancelled),
                }),
        };
        using var host = await CreateHost(client, first, "preserved draft");
        SetProperty(host, "Bonboarding", CreateBonboardingService());
        SetProperty(host, "Launcher", new EllaiLauncher());
        SetField(host, "IsActive", true);
        SetField(host, "ShowComposer", true);

        await InvokeAsync(host, "OnCancel");

        Assert.Equal("preserved draft", GetField<string?>(host, "InputText"));
        Assert.Equal(refreshed, GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.True(GetField<bool>(host, "IsActive"));
        Assert.True(GetField<bool>(host, "ShowComposer"));
        Assert.True(GetField<bool>(host, "_sessionVersionConflict"));
        Assert.False(GetField<bool>(host, "_cancelOutcomeUnknown"));
        Assert.Contains("Press Cancel again", GetField<string?>(host, "StatusText"));
        Assert.Single(client.Cancellations);
        Assert.Equal(("session-1", 1L), client.Cancellations[0]);

        await InvokeAsync(host, "OnCancel");

        Assert.Equal(2, client.Cancellations.Count);
        Assert.Equal(2, client.Cancellations[1].ExpectedSessionVersion);
        Assert.Null(GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.Equal("Ellai session ended.", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task CancelDuringPendingUtteranceCancelsRequestThenCancelsServerSession()
    {
        var enteredSubmit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            SubmitHandler = async (_, cancellationToken) =>
            {
                enteredSubmit.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Unavailable, null);
            },
            CancelResults = new Queue<EllaiSessionCancelResult>(new[]
            {
                new EllaiSessionCancelResult(EllaiClientOperationStatus.Cancelled),
            }),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "keep this draft");
        SetProperty(host, "Bonboarding", CreateBonboardingService());

        var sending = InvokeTask(host, "OnSend");
        await enteredSubmit.Task;
        await InvokeAsync(host, "OnCancel");
        await sending;

        Assert.Single(client.Submissions);
        Assert.Single(client.Cancellations);
        Assert.Null(GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.Equal("Ellai session ended.", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task CancelDuringStartCancelsLateCreatedSessionBeforeHiding()
    {
        var enteredStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            StartHandler = async (_, _) =>
            {
                enteredStart.TrySetResult();
                await finishStart.Task;
                return new EllaiSessionStartResult(
                    EllaiClientOperationStatus.Accepted,
                    CreateSession(version: 1));
            },
            CancelResults = new Queue<EllaiSessionCancelResult>(new[]
            {
                new EllaiSessionCancelResult(EllaiClientOperationStatus.Cancelled),
            }),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "");
        SetField(host, "_sessionView", null);
        SetField(host, "ShowComposer", false);
        SetProperty(host, "Bonboarding", CreateBonboardingService());

        var starting = InvokeTask(host, "SelectModeAsync", EllaiMode.Ask);
        await enteredStart.Task;
        await InvokeAsync(host, "OnCancel");
        finishStart.TrySetResult();
        await starting;

        Assert.Single(client.Cancellations);
        Assert.Null(GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.Equal("Ellai session ended.", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public void PositionResolveClampsInsideViewport()
    {
        var inside = EllaiComposerPosition.Resolve(new EllaiComposerPosition(100, 100), 1280, 800);
        Assert.Equal(100, inside.X);
        Assert.Equal(100, inside.Y);

        var outside = EllaiComposerPosition.Resolve(new EllaiComposerPosition(-50, 2000), 1280, 800);
        Assert.Equal(0, outside.X);
        Assert.Equal(640, outside.Y);

        var tiny = EllaiComposerPosition.Resolve(new EllaiComposerPosition(500, 500), 100, 100);
        Assert.Equal(0, tiny.X);
        Assert.Equal(0, tiny.Y);
    }

    [Fact]
    public void PositionMoveAndResetBehave()
    {
        var moved = EllaiComposerPosition.Default.Move(24, -24);
        Assert.Equal(EllaiComposerPosition.Default.X + 24, moved.X);

        var reset = EllaiComposerPosition.Default;
        Assert.Equal(0, reset.X);
        Assert.Equal(0, reset.Y);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Discovering")]
    [InlineData("Planning")]
    [InlineData("NeedsInput")]
    [InlineData("Ready")]
    [InlineData("Active")]
    [InlineData("Replanning")]
    [InlineData("Blocked")]
    public void KnownPhasesMapToLabels(string phase)
    {
        Assert.Equal(phase, EllaiStatusPhases.Label(phase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chatting")]
    public void UnknownPhasesRenderNothing(string? phase)
    {
        Assert.Null(EllaiStatusPhases.Label(phase));
    }

    private static async Task<string> RenderPublicLayoutAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddAuthorizationCore();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        services.AddSingleton<AuthenticationStateProvider>(new UnauthenticatedStateProvider());
        BitBonboarding.AddBitBonboardingServices(services, _ => { });
        BitBonboarding.AddBitBonboardingInMemoryStore(services);
        services.AddSingleton<IBonboardingService>(CreateBonboardingService());
        services.AddSingleton<IEllaiGuidanceControl>(CreateGuidanceControl());
        services.AddScoped<EllaiLauncher>();
        var reviewClient = new FakeReviewClient();
        services.AddSingleton<IEllaiReviewClient>(reviewClient);
        services.AddSingleton(new EllaiEventPoller(reviewClient));

        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        RenderFragment publicLayout = builder =>
        {
            builder.OpenComponent<PublicLayout>(0);
            builder.AddAttribute(1, nameof(PublicLayout.ChildContent), (RenderFragment)(content =>
                content.AddContent(0, "Public page")));
            builder.CloseComponent();
        };

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CascadingAuthenticationState>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CascadingAuthenticationState.ChildContent)] = publicLayout,
                }));

            return component.ToHtmlString();
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ComposerOffersIndependentMicrophoneToggleWhenVoiceIsEnabled(bool voiceOn, bool microphoneOn)
    {
        var callback = EventCallback.Factory.Create(new object(), () => { });
        var html = await RenderComposerAsync(isListeningEnabled: voiceOn, isLiveSpeaking: microphoneOn,
            onStartRealtimeVoice: callback, onStopVoice: callback);
        Assert.Equal(voiceOn, html.Contains("data-ellai-microphone-toggle", StringComparison.Ordinal));
        if (voiceOn)
        {
            Assert.Contains(microphoneOn ? "Turn off microphone" : "Turn on microphone", html);
            Assert.Contains(microphoneOn ? "Mic on" : "Mic off", html);
            Assert.Contains("Turn off Ellai voice", html);
        }
    }

    private static async Task<string> RenderComposerAsync(
        string? inputText = "test question",
        string? responseText = null,
        string? livePartialTranscript = null,
        bool canUseMicrophone = true,
        bool? canUseRealtimeMicrophone = null,
        EventCallback onStartVoice = default,
        EventCallback onStartRealtimeVoice = default,
        EventCallback onOpenSettings = default,
        bool isLiveSpeaking = true,
        bool isListeningEnabled = false,
        string? sentMessage = null,
        EventCallback onStopVoice = default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<EllaiCompactComposer>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(EllaiCompactComposer.InputText)] = inputText,
                    [nameof(EllaiCompactComposer.CanPlayResponse)] = true,
                    [nameof(EllaiCompactComposer.IsListeningEnabled)] = isListeningEnabled,
                    [nameof(EllaiCompactComposer.SentMessage)] = sentMessage,
                    [nameof(EllaiCompactComposer.LivePartialTranscript)] = livePartialTranscript,
                    [nameof(EllaiCompactComposer.ResponseText)] = responseText,
                    [nameof(EllaiCompactComposer.StatusText)] = "Messaging is unavailable.",
                    [nameof(EllaiCompactComposer.CanSend)] = false,
                    [nameof(EllaiCompactComposer.CanUseMicrophone)] = canUseMicrophone,
                    [nameof(EllaiCompactComposer.CanUseRealtimeMicrophone)] =
                        canUseRealtimeMicrophone ?? canUseMicrophone,
                    [nameof(EllaiCompactComposer.IsLiveSpeaking)] = isLiveSpeaking,
                    [nameof(EllaiCompactComposer.OnStartVoice)] = onStartVoice,
                    [nameof(EllaiCompactComposer.OnStartRealtimeVoice)] = onStartRealtimeVoice,
                    [nameof(EllaiCompactComposer.OnStopVoice)] = onStopVoice,
                    [nameof(EllaiCompactComposer.OnOpenSettings)] = onOpenSettings,
                }));

            return component.ToHtmlString();
        });
    }

    private static EllaiVoiceTurnStatusDto CompletedVoiceTurnStatus(string transcript) => new(
        "TurnCompleted", 1, 1_200, 700, null,
        [new EllaiCompletedVoiceTurnDto(
            1, "muse-turn-1", transcript, "session-1", Guid.NewGuid().ToString("N"), 1,
            AudioStartSample: 0, AudioEndSample: 1_200)],
        "session-1");

    private static EllaiSessionView CreateSession(long version) => new(
        "session-1", EllaiMode.Ask, null, "Active", "scope-1", "consent/1", "caps/1",
        0, 12, null, 0, version);

    private static async Task<TestComponentHost<EllaiGuidanceHost>> CreateHost(
        FakeReviewClient client,
        EllaiSessionView session,
        string inputText, EllaiDisplayedReceiptContext? receiptContext = null)
    {
        var services = new ServiceCollection();
        if (receiptContext is not null) services.AddSingleton(receiptContext);
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        BitBonboarding.AddBitBonboardingServices(services, _ => { });
        BitBonboarding.AddBitBonboardingInMemoryStore(services);
        services.AddSingleton<IBonboardingService>(CreateBonboardingService());
        services.AddSingleton<IEllaiGuidanceControl>(CreateGuidanceControl());
        services.AddSingleton<EllaiLauncher>();
        services.AddSingleton<IEllaiReviewClient>(client);
        var eventPoller = new EllaiEventPoller(client);
        services.AddSingleton(eventPoller);

        var serviceProvider = services.BuildServiceProvider();
        var renderer = new StateTestRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());
        var testHost = await renderer.RenderAsync<EllaiGuidanceHost>();
        SetField(testHost, "_sessionView", session);
        SetField(testHost, "InputText", inputText);
        if (session is not null)
        {
            await renderer.Dispatcher.InvokeAsync(() => eventPoller.TrackSession(session.SessionId));
        }

        return testHost;
    }

    private static async Task InvokeAsync(object target, string methodName, params object?[] parameters)
    {
        await InvokeTask(target, methodName, parameters);
    }

    private static Task InvokeTask(object target, string methodName, params object?[] parameters)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        return testHost.Dispatcher.InvokeAsync(() => InvokeTaskCore(testHost.Component, methodName, parameters));
    }

    private static Task InvokeTaskCore(object target, string methodName, params object?[] parameters)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target method {methodName}.");
        var result = method.Invoke(target, parameters);
        if (result is Task task)
        {
            return task;
        }

        if (method.ReturnType == typeof(void))
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException($"Test target {methodName} did not return a task or void.");
    }

    private static T GetField<T>(object target, string fieldName)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        return testHost.Dispatcher.InvokeAsync(() => GetFieldCore<T>(testHost.Component, fieldName))
            .GetAwaiter().GetResult();
    }

    private static T GetFieldCore<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target field {fieldName}.");
        return (T)field.GetValue(target)!;
    }

    private static T GetProperty<T>(object target, string propertyName)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        return testHost.Dispatcher.InvokeAsync(() => GetPropertyCore<T>(testHost.Component, propertyName))
            .GetAwaiter().GetResult();
    }

    private static T GetPropertyCore<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target property {propertyName}.");
        return (T)property.GetValue(target)!;
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        testHost.Dispatcher.InvokeAsync(() => SetFieldCore(testHost.Component, fieldName, value))
            .GetAwaiter().GetResult();
    }

    private static void SetFieldCore(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target field {fieldName}.");
        field.SetValue(target, value);
    }

    private static void SetProperty(object target, string propertyName, object? value)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        testHost.Dispatcher.InvokeAsync(() => SetPropertyCore(testHost.Component, propertyName, value))
            .GetAwaiter().GetResult();
    }

    private static void SetPropertyCore(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target property {propertyName}.");
        property.SetValue(target, value);
    }

    private static IEllaiGuidanceControl CreateGuidanceControl() =>
        DispatchProxy.Create<IEllaiGuidanceControl, NoOpGuidanceControlProxy>();

    private static IBonboardingService CreateBonboardingService() =>
        DispatchProxy.Create<IBonboardingService, NoOpBonboardingServiceProxy>();

    private sealed class UnauthenticatedStateProvider : AuthenticationStateProvider
    {
        private static readonly AuthenticationState State = new(
            new ClaimsPrincipal(new ClaimsIdentity()));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(State);
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        private static readonly IJSObjectReference Module = new NoOpJsModule();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeCore<TValue>(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            InvokeCore<TValue>(identifier);

        private static ValueTask<TValue> InvokeCore<TValue>(string identifier)
        {
            if (identifier == "import" && typeof(TValue) == typeof(IJSObjectReference))
            {
                return ValueTask.FromResult((TValue)Module);
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class ObservationJsModule : IJSObjectReference
    {
        public string ControlId { get; init; } = "report.entry";
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)new UiObservationReport("view", "context", "web", [ControlId],
                [new(ControlId, UiObservationState.Ready)]));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpJsModule : IJSObjectReference
    {
        public static List<string> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            RecordAndReturn<TValue>(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            RecordAndReturn<TValue>(identifier);

        private static ValueTask<TValue> RecordAndReturn<TValue>(string identifier)
        {
            Invocations.Add(identifier);
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingJsModule : IJSObjectReference
    {
        public List<string> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Invocations.Add(identifier);
            var result = identifier == "startCapture" && typeof(TValue) == typeof(string)
                ? (TValue)(object)"Microphone active; test capture."
                : default!;
            return ValueTask.FromResult(result);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingNarrationJsModule : IJSObjectReference
    {
        private readonly TaskCompletionSource _ackStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void FinishFirstPlayback() => _ackStopped.TrySetResult();
        public TaskCompletionSource StopCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts { get; private set; }
        public int MaximumConcurrent { get; private set; }
        public bool StopRequested { get; private set; }
        private int _active;
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "playResponse")
            {
                Starts++;
                MaximumConcurrent = Math.Max(MaximumConcurrent, ++_active);
                if (Starts == 1) await _ackStopped.Task;
                _active--;
            }
            if (identifier == "stopResponse")
            {
                StopRequested = true;
                _ackStopped.TrySetResult();
                await StopCompleted.Task;
            }
            return default!;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TabNavigationManager : NavigationManager
    {
        public TabNavigationManager(string path) => Initialize("http://localhost/", "http://localhost/" + path);
        public int Moves { get; private set; }
        protected override void NavigateToCore(string uri, bool forceLoad) { Moves++; Uri = ToAbsoluteUri(uri).AbsoluteUri; }
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            NotifyLocationChanged(isInterceptedLink: false);

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            NotifyLocationChanged(isInterceptedLink: false);
    }

#pragma warning disable BL0006 // The minimal test renderer must use Blazor's renderer APIs to exercise a mounted component.
    private sealed class StateTestRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<TestComponentHost<TComponent>> RenderAsync<TComponent>()
            where TComponent : IComponent
        {
            TComponent component = default!;
            var componentId = -1;
            await Dispatcher.InvokeAsync(async () =>
            {
                component = (TComponent)InstantiateComponent(typeof(TComponent));
                componentId = AssignRootComponentId(component);
                await RenderRootComponentAsync(componentId);
            });

            return new TestComponentHost<TComponent>(this, component, componentId);
        }

        public void Unmount(int componentId) => RemoveRootComponent(componentId);

        protected override void HandleException(Exception exception) => throw exception;

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
#pragma warning restore BL0006

    private sealed class TestComponentHost<TComponent>(
        StateTestRenderer renderer,
        TComponent component,
        int componentId) : IDisposable
        where TComponent : IComponent
    {
        public TComponent Component { get; } = component;

        public Dispatcher Dispatcher => renderer.Dispatcher;

        public void Dispose() => renderer.Dispatcher.InvokeAsync(() => renderer.Unmount(componentId))
            .GetAwaiter().GetResult();
    }

    private sealed class FakeReviewClient(params EllaiUtteranceSubmitResult[] submitResults)
        : IEllaiReviewClient
    {
        public int GuidancePlanCalls { get; private set; }
        public EllaiSessionStartResult ResumeResult { get; init; } = new(EllaiClientOperationStatus.Unavailable);
        public Task<EllaiSessionStartResult> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(ResumeResult);
        public List<EllaiGuidanceControlRequest> GuidanceControls { get; } = [];
        public Task<EllaiGuidanceProgressDto?> ControlGuidanceAsync(
            string sessionId, EllaiGuidanceControlRequest request, CancellationToken cancellationToken)
        {
            GuidanceControls.Add(request);
            return Task.FromResult<EllaiGuidanceProgressDto?>(new(request.Command == "cancel" ? "Cancelled" : "Paused"));
        }

        public Func<Task<EllaiGuidancePlanDto?>>? GuidancePlanHandler { get; init; }
        public int GuidanceNarrationCalls { get; private set; }
        public Task<EllaiSessionResponseDto?> PrepareGuidanceNarrationAsync(
            string sessionId, EllaiGuidanceNarrationRequest request, CancellationToken cancellationToken)
        {
            GuidanceNarrationCalls++;
            return Task.FromResult<EllaiSessionResponseDto?>(null);
        }
        public Task<EllaiGuidancePlanDto?> PrepareGuidancePlanAsync(string sessionId, string proposalId, CancellationToken cancellationToken)
        {
            GuidancePlanCalls++;
            return GuidancePlanHandler?.Invoke() ?? Task.FromResult<EllaiGuidancePlanDto?>(new("NeedsObservation"));
        }

        public IReadOnlyList<EllaiConversationTurnDto>? SavedConversation { get; init; }
        public string? HistorySessionId { get; private set; }
        public Task<EllaiSessionResponsePageDto?> PollResponsesAsync(string sessionId, long cursor, CancellationToken cancellationToken)
        {
            HistorySessionId = sessionId;
            return Task.FromResult<EllaiSessionResponsePageDto?>(SavedConversation is null ? null : new([], 0, Conversation: SavedConversation));
        }

        private readonly Queue<EllaiUtteranceSubmitResult> _submitResults = new(submitResults);

        public List<SubmitUtteranceRequest> Submissions { get; } = [];

        public List<(string SessionId, byte[] Audio, EllaiVoiceCaptureMode Mode,
            string? VoiceCaptureId, long VoiceCaptureOffsetSamples)> VoiceTranscriptions { get; } = [];

        public int LiveTranscriptionCalls { get; private set; }

        public Func<string, byte[], EllaiVoiceCaptureMode, CancellationToken,
            Task<EllaiVoiceTranscriptionResult?>>? VoiceTranscriptionHandler { get; init; }

        public Func<string, Stream, Task<EllaiVoiceTranscriptionResult?>>? LiveTranscriptionHandler { get; init; }

        public Func<string, CancellationToken, Task<EllaiVoiceTurnStatusDto?>>? VoiceTurnStatusHandler { get; init; }

        public int VoiceTurnStatusReads { get; private set; }

        public Task<EllaiVoiceTurnStatusDto?> GetVoiceTurnStatusAsync(
            string sessionId, CancellationToken cancellationToken, long afterSequence = 0)
        {
            VoiceTurnStatusReads++;
            return VoiceTurnStatusHandler?.Invoke(sessionId, cancellationToken)
                ?? Task.FromResult<EllaiVoiceTurnStatusDto?>(null);
        }

        public int ProductHelpConsentGrants { get; private set; }

        public List<(string SessionId, long ExpectedSessionVersion)> Cancellations { get; } = [];

        public int SessionStartCalls { get; private set; }

        public Queue<EllaiSessionCancelResult> CancelResults { get; init; } = new();

        public Func<EllaiMode, CancellationToken, Task<EllaiSessionStartResult>>? StartHandler { get; init; }

        public Func<SubmitUtteranceRequest, CancellationToken, Task<EllaiUtteranceSubmitResult>>? SubmitHandler { get; init; }

        public IReadOnlyList<EllaiApprovedReceiptDto>? ListedReceipts { get; init; } = [];

        public int ApprovalCalls { get; private set; }

        public int ReceiptListCalls { get; private set; }

        public EllaiReviewDto? ActiveReview { get; init; }

        public int ActiveReviewReads { get; private set; }

        public int RejectionCalls { get; private set; }

        public bool RejectionResult { get; init; }

        public Exception? RejectionException { get; init; }

        public Task<bool> GrantVoiceCaptureConsentAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<EllaiVoiceTranscriptionResult?> TranscribeVoiceAsync(
            string sessionId,
            byte[] audio,
            EllaiVoiceCaptureMode mode,
            CancellationToken cancellationToken,
            string? voiceCaptureId = null,
            long voiceCaptureOffsetSamples = 0)
        {
            VoiceTranscriptions.Add((sessionId, audio.ToArray(), mode, voiceCaptureId, voiceCaptureOffsetSamples));
            return VoiceTranscriptionHandler?.Invoke(sessionId, audio, mode, cancellationToken)
                ?? Task.FromResult<EllaiVoiceTranscriptionResult?>(null);
        }

        public Task<EllaiVoiceTranscriptionResult?> TranscribeLiveVoiceStreamAsync(
            string sessionId,
            Stream audio,
            CancellationToken cancellationToken,
            string? voiceCaptureId = null,
            long voiceCaptureOffsetSamples = 0)
        {
            LiveTranscriptionCalls++;
            return LiveTranscriptionHandler?.Invoke(sessionId, audio)
                ?? Task.FromResult<EllaiVoiceTranscriptionResult?>(null);
        }

        public Task<EllaiSessionStartResult> StartSessionAsync(EllaiMode mode, CancellationToken cancellationToken)
        {
            SessionStartCalls++;
            return StartHandler?.Invoke(mode, cancellationToken)
                ?? Task.FromResult(new EllaiSessionStartResult(EllaiClientOperationStatus.OutcomeUnknown));
        }

        public Task<EllaiUtteranceSubmitResult> SubmitUtteranceAsync(
            SubmitUtteranceRequest request,
            CancellationToken cancellationToken)
        {
            Submissions.Add(request);
            if (SubmitHandler is not null)
            {
                return SubmitHandler(request, cancellationToken);
            }

            return Task.FromResult(_submitResults.Dequeue());
        }

        public Task<bool> GrantPublicProductHelpConsentAsync(
            string sessionId, CancellationToken cancellationToken)
        {
            ProductHelpConsentGrants++;
            return Task.FromResult(true);
        }

        public Task<EllaiSessionCancelResult> CancelSessionAsync(
            string sessionId,
            long expectedSessionVersion,
            CancellationToken cancellationToken)
        {
            Cancellations.Add((sessionId, expectedSessionVersion));
            return Task.FromResult(CancelResults.Dequeue());
        }

        public Task<EllaiSessionEventPageDto?> PollEventsAsync(
            string sessionId,
            long cursor,
            int waitSeconds,
            CancellationToken cancellationToken) =>
            Task.FromResult<EllaiSessionEventPageDto?>(null);

        public Task<EllaiReviewDto?> GetActiveReviewAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            ActiveReviewReads++;
            return Task.FromResult(ActiveReview);
        }

        public Func<Task<bool>>? ObservationHandler { get; init; }
        public Task<bool> ReportUiObservationAsync(
            string sessionId,
            UiObservationReport report,
            CancellationToken cancellationToken) => ObservationHandler?.Invoke() ?? Task.FromResult(false);

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ListReceiptsAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            ReceiptListCalls++;
            return Task.FromResult(ListedReceipts);
        }

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
            string sessionId,
            string requestId,
            IReadOnlyList<string> actionIds,
            CancellationToken cancellationToken)
        {
            ApprovalCalls++;
            return Task.FromResult<IReadOnlyList<EllaiApprovedReceiptDto>?>(null);
        }

        public Task<bool> RejectReviewAsync(
            string sessionId,
            string requestId,
            CancellationToken cancellationToken)
        {
            RejectionCalls++;
            if (RejectionException is not null)
            {
                return Task.FromException<bool>(RejectionException);
            }

            return Task.FromResult(RejectionResult);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuidanceCountdownAdvancesOnceOrCancelsWhenUserTypes(bool interrupted)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "step" };
        SetField(host, "_generatedTour", tour);
        SetField(host, "_guidanceNarrationEventId", "plan:step:1");
        SetField(host, "_latestResponseSequence", 1L);
        SetField(host, "_isPlayingResponse", true);
        await InvokeAsync(host, "BeginGuidanceCountdown", 1L);
        Assert.Null(GetField<int?>(host, "_guidanceCountdownSeconds"));
        SetField(host, "_isPlayingResponse", false);
        await InvokeAsync(host, "BeginGuidanceCountdown", 1L);
        Assert.Equal(5, GetField<int?>(host, "_guidanceCountdownSeconds"));
        if (interrupted) await InvokeAsync(host, "OnInputChanged", "My next question");
        await Task.Delay(5500);
        Assert.Equal(interrupted ? 0 : 1, proxy.Calls.Count(call => call == "NextAsync"));
        Assert.Null(GetField<int?>(host, "_guidanceCountdownSeconds"));
    }

    [Fact]
    public async Task GuidanceCountdownIgnoresStaleAudioAndPausedTours()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), string.Empty);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        SetField(host, "_generatedTour", tour);
        SetField(host, "_guidanceNarrationEventId", "current");
        SetField(host, "_latestResponseSequence", 2L);
        await InvokeAsync(host, "BeginGuidanceCountdown", 1L);
        Assert.Null(GetField<int?>(host, "_guidanceCountdownSeconds"));
        proxy.TourStatus = BonboardingTourStatus.Paused;
        await InvokeAsync(host, "BeginGuidanceCountdown", 2L);
        Assert.Null(GetField<int?>(host, "_guidanceCountdownSeconds"));
        Assert.Empty(proxy.Calls);
    }

    [Theory]
    [InlineData("alternative", "", 1)]
    [InlineData("unknown", "", 0)]
    [InlineData("alternative", "unsent user draft", 0)]
    public async Task GuidanceSuggestionUsesNormalDurableSubmissionWithoutOverwritingDrafts(string choice, string draft, int submissions)
    {
        var client = new FakeReviewClient(new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Accepted, CreateSession(2)));
        using var host = await CreateHost(client, CreateSession(1), draft);
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "step" };
        var step = new GuidancePlanStep("step", "report.entry", "open-report-form", PlanStepBehavior.Explain,
            null, null, [], [], "Read the heading.", null) { SuggestionIds = ["alternative"] };
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["step"], [], [], [step], [], "catalogue", "contract")
        {
            Suggestions = new Dictionary<string, GuidanceStepSuggestion> { ["alternative"] = new("Explain the form", "report.entry", "open-report-form") }
        });
        SetField(host, "_controlGeneratedTour", (Func<string, Task<bool>>)(command =>
        {
            Assert.Equal("pause", command);
            proxy.TourStatus = BonboardingTourStatus.Paused;
            return Task.FromResult(true);
        }));
        await InvokeAsync(host, "SelectGuidanceSuggestionAsync", choice);
        Assert.Equal(submissions, client.Submissions.Count);
        if (submissions == 1)
        {
            Assert.Contains("new guided plan", client.Submissions[0].FinalizedText);
            Assert.Contains("open-report-form", client.Submissions[0].FinalizedText);
        }
        else Assert.Equal(draft, GetField<string?>(host, "InputText"));
    }

    [Fact]
    public async Task ValidationGuidanceHighlightsOneTrustedFieldAtATimeAndRestoresTheStep()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), "");
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "step" };
        var step = new GuidancePlanStep("step", "report.narrative", "record-observations", PlanStepBehavior.GuideHuman,
            null, null, [], [], "Complete the report.", null);
        var anchors = new Dictionary<string, string> { ["report.narrative"] = EllaiReportUiManifest.AnchorFor("report.narrative") };
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["step"], [], [], [step], [], "catalogue", "contract"));
        SetField(host, "_guidanceFeatureId", EllaiReportUiManifest.FeatureId);
        SetField(host, "_guidanceAnchors", anchors);
        UiObservationReport Observation(params string[] ids) => new("view", "context", "web", [], []) { ValidationInputIds = ids };

        SetField(host, "_latestGuidanceObservation", Observation("untrusted.input", "report.original-statement", "report.occurrence-date"));
        await InvokeAsync(host, "UpdateValidationGuidanceAsync", CancellationToken.None);
        Assert.Equal("ellai-report-original-statement", anchors["report.narrative"]);
        Assert.Contains("Describe what happened", GetField<string>(host, "_validationGuidanceText"));
        Assert.Contains("GoToAsync", proxy.Calls);
        var retries = 0;
        SetField(host, "_narrateValidation", (Func<string?, string?, CancellationToken, Task>)((inputId, context, _) =>
        {
            Assert.Equal("report.original-statement", inputId);
            Assert.Equal("context", context);
            retries++;
            return Task.CompletedTask;
        }));
        SetField(host, "_guidanceNarrationRetryPending", true);
        SetField(host, "_guidanceNarrationRetryAtUtc", DateTimeOffset.UtcNow.AddSeconds(10));
        await InvokeAsync(host, "UpdateValidationGuidanceAsync", CancellationToken.None);
        Assert.Equal(0, retries);
        SetField(host, "_guidanceNarrationRetryAtUtc", DateTimeOffset.UtcNow.AddSeconds(-1));
        await InvokeAsync(host, "UpdateValidationGuidanceAsync", CancellationToken.None);
        Assert.Equal(1, retries);
        SetField(host, "_narrateValidation", null);

        SetField(host, "_latestGuidanceObservation", Observation("report.occurrence-date"));
        await InvokeAsync(host, "UpdateValidationGuidanceAsync", CancellationToken.None);
        Assert.Equal("ellai-report-occurrence-date", anchors["report.narrative"]);
        Assert.Contains("reported date", GetField<string>(host, "_validationGuidanceText"));

        SetField(host, "_latestGuidanceObservation", Observation());
        await InvokeAsync(host, "UpdateValidationGuidanceAsync", CancellationToken.None);
        Assert.Equal(EllaiReportUiManifest.AnchorFor("report.narrative"), anchors["report.narrative"]);
        Assert.Null(GetField<string?>(host, "_validationGuidanceText"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingGuidanceTargetOffersRecoveryUnlessInputWasAlreadyValidated(bool completed, bool recovery)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), "");
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Waiting;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "step" };
        var step = new GuidancePlanStep("step", "report.subject", "choose-report-subject", PlanStepBehavior.GuideHuman,
            null, null, [], [], "Choose the subject.", null);
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["step"], [], [], [step], [], "catalogue", "contract"));
        SetField(host, "_latestGuidanceObservation", new UiObservationReport("view", "context", "web", [], [],
            CompletedControlIds: completed ? ["report.subject"] : []));

        await InvokeAsync(host, "UpdateUnavailableGuidanceTarget");

        Assert.Equal(recovery, GetField<bool>(host, "_guidanceReplanRequired"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuidanceRecoversWhenObservedTargetReturnsOrCompletes(bool completed)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), "");
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Waiting;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "step" };
        var step = new GuidancePlanStep("step", "report.subject", "choose-report-subject", PlanStepBehavior.GuideHuman,
            null, null, [], [], "Choose the subject.", null);
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["step"], [], [], [step], [], "catalogue", "contract"));
        SetField(host, "_latestGuidanceObservation", new UiObservationReport("view", "context", "web", [], []));
        await InvokeAsync(host, "UpdateUnavailableGuidanceTarget");
        Assert.True(GetField<bool>(host, "_guidanceReplanRequired"));

        SetField(host, "_latestGuidanceObservation", completed
            ? new UiObservationReport("view", "context", "web", [], [], CompletedControlIds: ["report.subject"])
            : new UiObservationReport("view", "context", "web", ["report.subject"], [new("report.subject", UiObservationState.Ready)]));
        await InvokeAsync(host, "UpdateUnavailableGuidanceTarget");
        Assert.False(GetField<bool>(host, "_guidanceReplanRequired"));
        Assert.Null(GetField<string?>(host, "_guidanceNotice"));
    }

    [Fact]
    public async Task FindingAnotherWayReplansSameConversationWithoutCancellingItsCheckpointOrDraft()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(1), "unsent draft");
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        SetField(host, "_generatedTour", tour);
        SetField(host, "_guidanceProposalId", "proposal");
        SetField(host, "_guidanceFeatureId", "report");
        SetField(host, "_guidanceProposalIsAccepted", true);
        SetField(host, "_guidanceReplanRequired", true);

        await InvokeAsync(host, "ReplanGeneratedGuidanceAsync");

        Assert.Contains("CancelAsync", proxy.Calls);
        Assert.Equal(1, client.GuidancePlanCalls);
        Assert.Empty(client.GuidanceControls);
        Assert.Equal("unsent draft", GetField<string>(host, "InputText"));
        Assert.Equal("proposal", GetField<string>(host, "_guidanceProposalId"));
    }

    [Fact]
    public async Task FailedValidationNarrationCanRetryButExplicitStopDoesNotRestartIt()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(1), "");
        var tour = DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "step" };
        var step = new GuidancePlanStep("step", "report.narrative", "record-observations", PlanStepBehavior.GuideHuman,
            null, null, [], [], "Complete the report.", null);
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["step"], [], [], [step], [], "catalogue", "contract"));
        SetField(host, "_guidanceProposalId", "proposal");
        SetField(host, "_validationInputId", "report.original-statement");
        SetField(host, "_validationGuidanceText", "Describe what happened.");

        await InvokeAsync(host, "NarrateGuidanceStepAsync", tour, 1L, "report.original-statement", "context");
        Assert.Equal(1, client.GuidanceNarrationCalls);
        Assert.True(GetField<bool>(host, "_guidanceNarrationRetryPending"));
        Assert.Null(GetField<string?>(host, "_guidanceNarrationEventId"));

        await InvokeAsync(host, "NarrateGuidanceStepAsync", tour, 1L, "report.original-statement", "context");
        Assert.Equal(2, client.GuidanceNarrationCalls);
        await InvokeAsync(host, "EndGuidanceNarration");
        Assert.False(GetField<bool>(host, "_guidanceNarrationRetryPending"));
    }

    public class NoOpBonboardingServiceProxy : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public BonboardingTourStatus? TourStatus { get; set; }
        public BonboardingStepDefinition? CurrentStep { get; set; }
        public Func<Task>? NextHandler { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Status" && TourStatus.HasValue) return TourStatus.Value;
            if (targetMethod?.Name == "get_CurrentStep") return CurrentStep;
            if (targetMethod?.Name is "PauseAsync" or "ResumeAsync" or "CancelAsync" or "NextAsync" or "GoToAsync") Calls.Add(targetMethod.Name);
            if (targetMethod?.Name == "NextAsync" && NextHandler is not null) return NextHandler();
            return targetMethod?.ReturnType == typeof(void)
                ? null
                : targetMethod?.ReturnType == typeof(Task)
                    ? Task.CompletedTask
                : targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
        }
    }

    public class NoOpGuidanceControlProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(Task)
                ? Task.CompletedTask
                : targetMethod?.ReturnType == typeof(Task<GuidanceSnapshot>)
                    ? Task.FromResult<GuidanceSnapshot>(null!)
                    : targetMethod?.ReturnType == typeof(Task<GuidanceSnapshot?>)
                        ? Task.FromResult<GuidanceSnapshot?>(null)
                        : targetMethod?.ReturnType.IsValueType == true
                            ? Activator.CreateInstance(targetMethod.ReturnType)
                            : null;
    }
}
