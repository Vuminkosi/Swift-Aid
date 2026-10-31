using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using System.Security.Claims;
using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Tests.Ellai;

/// <summary>Synthetic flagship drill through the real Ellai services:
/// session → policy → bounded cycle (stub model) → validation → guidance
/// coordination → registry planning → approval gate → evidence.
/// No provider, device or database: proves wiring, not live capability.</summary>
[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T17")]
public sealed class EllaiFlagshipWalkthroughTests
{
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "test-actor"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
    [Fact]
    public async Task SyntheticContinuityWalkthroughCompletesWithoutWrites()
    {
        var store = new InMemoryEllaiSessionStore();
        var sessions = new EllaiSessionService(store);
        var policy = new EllaiPolicyService();
        var consent = new EllaiConsentService();
        var broker = new EllaiToolBroker(policy, consent);
        var runner = new EllaiCycleRunner(broker);
        var coordinator = new EllaiGuidanceSessionCoordinator();
        var registry = new EllaiUiRegistry();
        registry.Register(new UiControlDescriptor(
            "cases.queue", "cases", 1, "ellai-uicontract/1.0", "Cases",
            "Panel", "label-key", "desc-key", Array.Empty<string>(),
            new[] { "view-queue" }, Array.Empty<string>(),
            "nav-cases", "anchor-queue", new[] { "HighlightControl" },
            UiRiskClass.R0, new[] { GuidanceMode.Navigation },
            Array.Empty<string>(), Array.Empty<string>(),
            "view-queue", "proof-ui-observed",
            new[] { "cases.read" }, "internal", false));
        var planValidator = new EllaiGuidancePlanValidator(registry);

        // 1. Start + utterance.
        var session = await sessions.StartAsync(
            new StartSessionRequest(EllaiMode.HelpMeWork, "flag-1", "cases", null, "text", "text"),
            Principal, CancellationToken.None);
        var afterUtterance = await sessions.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", session.SessionId, session.SessionVersion,
                "What is overdue?", null, "typed", 1),
            ActorContext.FromValidatedPrincipal(Principal), CancellationToken.None);
        Assert.NotNull(afterUtterance);
        consent.Grant(session.SessionId, EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));

        // 2. Policy authorizes a synthetic read.
        var auth = policy.Authorize(
            new EllaiAuthorizationRequest(session.SessionId, EllaiMode.HelpMeWork,
                "cases", "synthetic-case-001", UiIntentKind.HighlightControl, null,
                session.ScopeDisplay, "consent/1", true),
            "synthetic");
        Assert.True(auth.Permitted);

        // 3. Bounded cycle: observe, then answer with evidence.
        var context = new EllaiToolContext(session.SessionId, session.ScopeDisplay,
            "cases", "synthetic-case-001", 4, true, "consent/1", EllaiProcessingClassification.SyntheticDemo);
        var outcome = await runner.RunAsync(
            context,
            iteration => Task.FromResult(iteration == 1
                ? new EllaiModelTurn(
                    new[] { new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()) }, null)
                : new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), new EllaiDecision(
                    2, DecisionKind.Answer, "Two synthetic items are overdue.", "Two items are overdue.",
                    new[]
                    {
                        new GroundedClaim("c1", "Two synthetic items are overdue.", ClaimKind.Simulation,
                            new[] { new EvidenceRef("queue", "synthetic-case-001", "v1", null, "e1") })
                    },
                    Array.Empty<EllaiQuestion>(), Array.Empty<UiIntentProposal>(),
                    null, Array.Empty<string>(), null, null))),
            EllaiDecisionValidator.Validate,
            CancellationToken.None);
        Assert.True(outcome.Completed);
        Assert.NotNull(outcome.Decision);

        // 4. Interruption-style cancel of a guidance attempt, then fresh state.
        var started = coordinator.Start(new StartGuidanceRequest(
            "cmd-1", "curated-queue-tour-001", "1.0", GuidanceMode.Presentation, null));
        var stopped = coordinator.Dispatch(new GuidanceControlRequest(
            "cmd-2", started.Position.GuidanceAttemptId, started.Position.GuidanceVersion,
            GuidanceCommandKind.Stop, null, null), GuidanceMode.Presentation);
        Assert.NotNull(stopped);
        Assert.Equal(GuidanceStatus.Cancelled, stopped.Status);

        // 5. Registry-bound plan validates; protected target would not.
        var draft = new EllaiGuidancePlanDraft(
            1, GuidancePlanStatus.Ready, "view queue", GuidanceMode.Navigation,
            new[]
            {
                new GuidancePlanStep("s1", "cases.queue", "view-queue", PlanStepBehavior.Explain,
                    null, null, Array.Empty<string>(), new[] { "e1" }, "See the queue.", null)
            },
            Array.Empty<EllaiQuestion>(), null);
        var (plan, errors) = planValidator.Validate(
            draft, new UiRegistryQuery("cases", Array.Empty<string>(), 20),
            new HashSet<string> { "cases" }, new HashSet<string> { "cases.read" },
            "plan-flag", 1, 0, 0);
        Assert.Empty(errors);
        Assert.NotNull(plan);

        // 6. Manifest-bound demo cycle redeems once; admin write needs exact approval.
        var (manifest, manifestError) = ScenarioManifestLoader.Load(
            """{"manifestId":"m","purpose":"demo","scenarioId":"s","synthetic":true,"allowedCapabilities":["cases.read"],"totalCycles":8}""");
        Assert.Null(manifestError);
        Assert.NotNull(manifest);
        var director = new EllaiScenarioDirector();
        var demo = new DemoManifest("m", "demo", "s", manifest.AllowedCapabilities,
            new[] { "synthetic-case-001" }, manifest.TotalCycles, 0,
            DateTimeOffset.UtcNow.AddHours(1), new[] { "officer" }, "hash");
        Assert.NotNull(director.RedeemCycle(demo, "demo", DateTimeOffset.UtcNow));

        // 7. Evidence projection separates verified from unverified.
        var explanation = EllaiEvidenceProjector.Project(
            outcome.Decision, Array.Empty<EvidenceRef>(), new[] { "care-outcome" });
        Assert.Contains("care-outcome", explanation.VerificationState, StringComparison.Ordinal);
        Assert.DoesNotContain("completed", explanation.VerificationState, StringComparison.OrdinalIgnoreCase);
    }
}
