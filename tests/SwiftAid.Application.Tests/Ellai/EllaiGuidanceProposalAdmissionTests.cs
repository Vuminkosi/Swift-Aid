using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiGuidanceProposalAdmissionTests
{
    [Theory]
    [InlineData("owner", "scope", true, "NeedsObservation")]
    [InlineData("other", "scope", true, "Denied")]
    [InlineData("owner", "old-scope", true, "Denied")]
    [InlineData("owner", "scope", false, "Denied")]
    public async Task AdmissionRequiresOwnerCurrentScopeAndFeatureGrant(string owner, string scope, bool allowed, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var authority = new EllaiUiEntitlements(owner, scope, allowed ? new HashSet<string> { "report" } : [], new HashSet<string>());
        Assert.Equal(expected, (await service.AdmitAsync("session", id, authority, default)).Status);
    }

    [Fact]
    public async Task FreshMountedEvidenceAllowsPlanningButANewerTurnFencesTheProposal()
    {
        var (service, store, observations, id) = await Fixture();
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready)]), default);
        var accepted = await service.AdmitAsync("session", id, authority, default);
        Assert.Equal("Ready", accepted.Status);
        Assert.Equal("Create a report", accepted.Objective);
        var current = (await store.GetAsync("session", default))!;
        await store.TryAcceptUtteranceAsync(current with { SessionVersion = 3 }, 2, "new-turn", default, "{}");
        Assert.Equal("Stale", (await service.AdmitAsync("session", id, authority, default)).Status);
    }

    [Theory]
    [InlineData(-60, UiObservationState.Ready, "NeedsObservation")]
    [InlineData(60, UiObservationState.Ready, "NeedsObservation")]
    [InlineData(0, UiObservationState.Disabled, "WaitingForControl")]
    [InlineData(0, UiObservationState.Hidden, "WaitingForControl")]
    public async Task PlanningWaitsForFreshActionableObservations(int offset, UiObservationState state, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry"], DateTimeOffset.UtcNow.AddSeconds(offset),
            [new("report.entry", state)]), default);
        Assert.Equal(expected, (await service.AdmitAsync("session", id,
            new("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>()), default)).Status);
    }

    [Theory]
    [InlineData(false, "Ready")]
    [InlineData(true, "Stale")]
    public async Task ModelPlanIsChargedAndRejectedWhenPageChangesDuringGeneration(bool changePage, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var now = DateTimeOffset.UtcNow;
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry"], now,
            [new("report.entry", UiObservationState.Ready)]), default);
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var usage = new InMemoryEllaiUsageStore();
        var planner = new EllaiModelGuidancePlanner(new PlannerCompletion(async () =>
        {
            if (changePage) await observations.RecordAsync(new("session", "other-view", "new-context", "web", ["report.entry"], now,
                [new("report.entry", UiObservationState.Ready)]), default);
        }));
        var result = await service.PlanAsync("session", id,
            new("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>()), planner,
            new EllaiUiDiscoveryService(), new EllaiGuidancePlanValidator(registry), new EllaiUsageLedger(), usage, default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(!changePage, result.Plan is not null);
        Assert.Equal(15, (await usage.TotalsAsync("session", default)).TotalTokens);
    }

    private sealed class PlannerCompletion(Func<Task> beforeReturn) : IEllaiTurnCompletionClient
    {
        public async Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            await beforeReturn();
            return new("""
                {"schemaVersion":1,"status":"Ready","objective":"Create a report","mode":"Navigation",
                 "steps":[{"stepKey":"entry","controlId":"report.entry","objectiveId":"open-report-form","behavior":"Explain",
                 "uiIntentBindingId":null,"resourceBindingKey":null,"factIds":["public-intake-no-account"],"evidenceIds":[],
                 "explanation":"This is the report form.","nextStepKey":null}],"questions":[],"stopReason":null}
                """, 10, 5);
        }
    }

    private static async Task<(EllaiGuidanceProposalAdmission Service, InMemoryEllaiSessionStore Store,
        InMemoryEllaiUiObservationStore Observations, string ProposalId)> Fixture()
    {
        var store = new InMemoryEllaiSessionStore();
        var session = new EllaiSessionRecord("session", EllaiMode.GuideMe, "Active", 1, 1, 1, 1, 0, 12,
            "owner", "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2 }, 1, "turn", default, "{}");
        var now = DateTimeOffset.UtcNow;
        var claim = (await store.ClaimNextAsync(now, now.AddMinutes(1), default))!;
        await store.CompleteAsync("session", "turn", claim.AttemptCount, now, default,
            EllaiUtteranceResolution.Completed("I can guide you", 1) with
            {
                UiProposalsJson = EllaiUiProposalPayload.Serialize([new(UiIntentKind.StartGuidance, "ellai.guidance", null, null,
                    new Dictionary<string, string> { ["feature"] = "report", ["objective"] = "Create a report" })])
            });
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var observations = new InMemoryEllaiUiObservationStore();
        var turn = Assert.Single(await store.ReadConversationAsync("session", "owner", default));
        return (new(store, store, registry, observations), store, observations,
            Assert.Single(EllaiConversationProjection.ToClient(turn).UiProposals).ProposalId);
    }
}
