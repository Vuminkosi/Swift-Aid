using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiGuidanceProposalAdmissionTests
{
    [Fact]
    public async Task ChangedObservationRevalidatesSamePlanBeforeAnExplanatoryStepAdvances()
    {
        var (service, store, observations, id) = await Fixture();
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var modelCalls = 0;
        var planner = new EllaiModelGuidancePlanner(new PlannerCompletion(() =>
        {
            modelCalls++;
            return Task.CompletedTask;
        }));
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var ledger = new EllaiUsageLedger();
        var usage = new InMemoryEllaiUsageStore();
        Task<EllaiGuidancePlanningResult> Prepare() => service.PlanAsync("session", id, authority, planner,
            new EllaiUiDiscoveryService(), new EllaiGuidancePlanValidator(registry), ledger, usage, default);

        await observations.RecordAsync(new("session", "view", "before", "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready)]), default);
        var first = await Prepare();
        Assert.Equal("Ready", first.Status);
        var checkpoint = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        await observations.RecordAsync(new("session", "view", "hidden", "web", ["report.entry", "report.subject"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Hidden), new("report.subject", UiObservationState.Ready)]), default);
        var cannotRebind = await service.PlanAsync("session", id, authority, planner, new EllaiUiDiscoveryService(),
            new EllaiGuidancePlanValidator(registry), ledger, usage, default, first.Plan!.PlanId, "entry");
        Assert.Equal("ReplanRequired", cannotRebind.Status);
        Assert.Equal(checkpoint.Version, EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!.Version);
        Assert.Equal(1, modelCalls);
        await observations.RecordAsync(new("session", "view", "after", "web", ["report.entry", "report.subject"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready), new("report.subject", UiObservationState.Ready)]), default);
        var request = new EllaiGuidanceStepRequest(id, first.Plan!.PlanId, checkpoint.Version, "entry", "after");
        Assert.Equal("Stale", (await service.CompleteStepAsync("session", request, authority, default)).Status);

        var rebound = await service.PlanAsync("session", id, authority, planner, new EllaiUiDiscoveryService(),
            new EllaiGuidancePlanValidator(registry), ledger, usage, default, first.Plan.PlanId, "entry");
        var current = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Equal(first.Plan.PlanId, rebound.Plan?.PlanId);
        Assert.Equal("after", current.ContextStamp);
        Assert.Equal(1, modelCalls);
        Assert.Equal("Completed", (await service.CompleteStepAsync("session", request with { ExpectedVersion = current.Version }, authority, default)).Status);
    }

    [Fact]
    public async Task WizardPlanContinuesAcrossValidatedTransitionsAndRestoresWithoutAnotherModelCall()
    {
        var (service, store, observations, id) = await Fixture();
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var completion = new WizardCompletion();
        var planner = new EllaiModelGuidancePlanner(completion);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var usage = new InMemoryEllaiUsageStore();
        var ledger = new EllaiUsageLedger();
        Task<EllaiGuidancePlanningResult> Restore() => service.PlanAsync("session", id, authority, planner,
            new EllaiUiDiscoveryService(), new EllaiGuidancePlanValidator(registry), ledger, usage, default);
        async Task Observe(string context, string[] mounted, string[] completed) =>
            await observations.RecordAsync(new("session", "view", context, "web", mounted, DateTimeOffset.UtcNow,
                mounted.Select(control => new UiControlState(control, UiObservationState.Ready)).ToArray(),
                CompletedControlIds: completed), default);
        await Observe("subject", ["report.entry", "report.subject"], []);
        Assert.Equal("Ready", (await Restore()).Status);
        foreach (var index in Enumerable.Range(0, 4))
        {
            var context = index == 0 ? "details" : "review";
            await Observe(context, index == 0 ? ["report.entry", "report.narrative", "report.time-location"] : ["report.entry", "report.review"],
                index == 0 ? ["report.subject"] : ["report.subject", "report.narrative", "report.time-location"]);
            var checkpoint = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
            var result = await service.CompleteStepAsync("session", new(id, checkpoint.Plan!.PlanId, checkpoint.Version,
                "s" + index, context), authority, default);
            Assert.Equal(index == 3 ? "Completed" : "Ready", result.Status);
            if (index < 3)
            {
                Assert.Equal("s" + (index + 1), result.CurrentStepId);
                Assert.Equal("Ready", (await Restore()).Status);
            }
        }
        Assert.Equal(1, completion.Calls);
        var final = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Equal("Completed", final.Status);
        Assert.Equal(4, final.CompletedStepIds.Count);
    }

    private sealed class WizardCompletion : IEllaiTurnCompletionClient
    {
        public int Calls { get; private set; }
        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            Calls++;
            var controls = new[] { "report.subject", "report.narrative", "report.time-location", "report.review" };
            var steps = controls.Select((id, index) =>
            {
                var control = EllaiReportUiManifest.All.Single(item => item.ControlId == id);
                return new GuidancePlanStep("s" + index, id, control.ObjectiveId!, index < 3 ? PlanStepBehavior.GuideHuman : PlanStepBehavior.Explain,
                    null, null, [], [], "Complete this form step.", index < 3 ? "s" + (index + 1) : null);
            }).ToArray();
            var json = System.Text.Json.JsonSerializer.Serialize(new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready,
                "Create a report", GuidanceMode.Navigation, steps, [], null), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            return Task.FromResult(new EllaiTurnCompletion(json, 10, 5));
        }
    }

    [Theory]
    [InlineData("report.subject", true, "Completed")]
    [InlineData("report.narrative", true, "Completed")]
    [InlineData("report.time-location", true, "Completed")]
    [InlineData("report.save", true, "Completed")]
    [InlineData("report.save", false, "EvidenceRequired")]
    [InlineData("report.subject", false, "EvidenceRequired")]
    [InlineData("report.submit", true, "EvidenceRequired")]
    public async Task GuideHumanRequiresRegisteredValidationProofAndCannotUseItForSubmission(string controlId, bool completed, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id, PlanStepBehavior.GuideHuman);
        var descriptor = EllaiReportUiManifest.All.Single(control => control.ControlId == controlId);
        checkpoint = checkpoint with { Plan = checkpoint.Plan! with { Steps =
            [checkpoint.Plan.Steps[0] with { ControlId = controlId, ObjectiveId = descriptor.ObjectiveId! }] } };
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, session.SessionVersion, default);
        // The wizard moved on: its title is mounted but the validated field is no longer in the DOM.
        var context = expected == "Completed" ? "validated-context" : "context";
        await observations.RecordAsync(new("session", "view", context, "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready)], CompletedControlIds: completed ? [controlId] : []), default);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var result = await service.CompleteStepAsync("session", new(id, checkpoint.Plan.PlanId, checkpoint.Version, "step-1", context), authority, default);
        Assert.Equal(expected, result.Status);
        var restored = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Equal(expected == "Completed" ? 1 : 0, restored.CompletedStepIds.Count);
    }

    [Theory]
    [InlineData(UiObservationState.Ready, "Completed")]
    [InlineData(UiObservationState.Hidden, "EvidenceRequired")]
    [InlineData(UiObservationState.Disabled, "EvidenceRequired")]
    [InlineData(UiObservationState.Ambiguous, "EvidenceRequired")]
    [InlineData(UiObservationState.Stale, "EvidenceRequired")]
    [InlineData(UiObservationState.Unknown, "EvidenceRequired")]
    public async Task AcceptedValidationCannotOverrideContradictoryLiveControlState(UiObservationState state, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id, PlanStepBehavior.GuideHuman);
        var descriptor = EllaiReportUiManifest.All.Single(control => control.ControlId == "report.subject");
        checkpoint = checkpoint with { Plan = checkpoint.Plan! with { Steps =
            [checkpoint.Plan.Steps[0] with { ControlId = descriptor.ControlId, ObjectiveId = descriptor.ObjectiveId! }] } };
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, session.SessionVersion, default);
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry", "report.subject"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready), new("report.subject", state)], CompletedControlIds: ["report.subject"]), default);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var result = await service.CompleteStepAsync("session", new(id, checkpoint.Plan.PlanId, checkpoint.Version, "step-1", "context"), authority, default);
        Assert.Equal(expected, result.Status);
        var restored = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Equal(expected == "Completed" ? 1 : 0, restored.CompletedStepIds.Count);
    }

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
    public async Task DurableProgressFollowsValidatedLinksRatherThanArrayOrder()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var first = checkpoint.Plan!.Steps[0] with { NextStepKey = "third" };
        checkpoint = checkpoint with { Plan = checkpoint.Plan with { Steps =
            [first, first with { StepKey = "second", NextStepKey = null }, first with { StepKey = "third", NextStepKey = "second" }] } };
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, session.SessionVersion, default);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        long version = checkpoint.Version;
        foreach (var (step, next) in new (string, string?)[] { ("step-1", "third"), ("third", "second"), ("second", null) })
        {
            var result = await service.CompleteStepAsync("session", new(id, checkpoint.Plan.PlanId, version, step, "context"), authority, default);
            Assert.Equal(next, result.CurrentStepId);
            Assert.Equal(next is null ? "Completed" : "Ready", result.Status);
            version = result.Version;
            var restored = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
            Assert.Equal(next, restored.CurrentStepId);
        }
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

    [Theory]
    [InlineData("refresh", "Ready")]
    [InlineData("none", "Stale")]
    [InlineData("changed", "Stale")]
    [InlineData("disabled", "Stale")]
    public async Task SlowPlanningUsesFreshUnchangedObservationWithoutRevivingStaleTargets(string update, string expected)
    {
        var (_, store, observations, id) = await Fixture();
        var clock = new PlanningClock(DateTimeOffset.UtcNow);
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var service = new EllaiGuidanceProposalAdmission(store, store, registry, observations, clock);
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry"], clock.Now,
            [new("report.entry", UiObservationState.Ready)]), default);
        var planner = new EllaiModelGuidancePlanner(new PlannerCompletion(async () =>
        {
            clock.Now = clock.Now.AddMinutes(1);
            if (update != "none")
                await observations.RecordAsync(new("session", "view", update == "changed" ? "new-context" : "context", "web",
                    ["report.entry"], clock.Now, [new("report.entry", update == "disabled" ? UiObservationState.Disabled : UiObservationState.Ready)]), default);
        }));
        var result = await service.PlanAsync("session", id,
            new("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>()), planner,
            new EllaiUiDiscoveryService(), new EllaiGuidancePlanValidator(registry), new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), default);
        Assert.Equal(expected, result.Status);
    }

    private sealed class PlanningClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplanningPreservesVerifiedReceiptBindingForTheSameProposal(bool providerFails)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var now = DateTimeOffset.UtcNow;
        var evidence = new EllaiGuidanceIntakeEvidence(Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(-10), now);
        checkpoint = checkpoint with { StartedAtUtc = now.AddMinutes(-1), IntakeEvidence = evidence };
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, session.SessionVersion, default);
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        // SeedProgress uses an old catalogue version, requiring regeneration.
        var planning = service.PlanAsync("session", id,
            new("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>()),
            new EllaiModelGuidancePlanner(new PlannerCompletion(() => providerFails ? Task.FromException(new HttpRequestException("synthetic provider failure")) : Task.CompletedTask)),
            new EllaiUiDiscoveryService(), new EllaiGuidancePlanValidator(registry), new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), default);
        if (providerFails) await Assert.ThrowsAsync<HttpRequestException>(() => planning);
        else Assert.Equal("Ready", (await planning).Status);
        var restored = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Equal(evidence, restored.IntakeEvidence);
        Assert.Equal(checkpoint.StartedAtUtc, restored.StartedAtUtc);
    }

    [Fact]
    public async Task ConcurrentPlanningUsesOneDurableLeaseAndCompletedPlanIsReused()
    {
        var (service, store, observations, id) = await Fixture();
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready)]), default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var planner = new EllaiModelGuidancePlanner(new PlannerCompletion(async () =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
        }));
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var usage = new InMemoryEllaiUsageStore();
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        Task<EllaiGuidancePlanningResult> Run() => service.PlanAsync("session", id, authority, planner,
            new EllaiUiDiscoveryService(), new EllaiGuidancePlanValidator(registry), new EllaiUsageLedger(), usage, default);
        var first = Run();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = Run();
        try
        {
            Assert.Equal("Generating", (await duplicate.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        }
        finally { release.TrySetResult(); }
        var accepted = await first;
        Assert.Equal("Ready", accepted.Status);
        var replay = await Run();
        Assert.Equal(accepted.Plan!.PlanId, replay.Plan!.PlanId);
        Assert.Equal(1, calls);
        Assert.Equal(15, (await usage.TotalsAsync("session", default)).TotalTokens);
        var durable = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Equal("Ready", durable.Status);
        Assert.Equal(accepted.Plan.Steps[0].StepKey, durable.CurrentStepId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledParentOrProviderFailureCannotLeaveAnActivatablePlan(bool providerFailure)
    {
        var (service, store, observations, id) = await Fixture();
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready)]), default);
        var planner = new EllaiModelGuidancePlanner(new PlannerCompletion(async () =>
        {
            if (providerFailure) throw new InvalidOperationException("Synthetic failure");
            var current = (await store.GetAsync("session", default))!;
            await store.TryUpdateAsync(current with { SessionVersion = current.SessionVersion + 1, Status = "Cancelled" },
                current.SessionVersion, default);
        }));
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var operation = service.PlanAsync("session", id,
            new("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>()), planner,
            new EllaiUiDiscoveryService(), new EllaiGuidancePlanValidator(registry), new EllaiUsageLedger(),
            new InMemoryEllaiUsageStore(), default);
        if (providerFailure) await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        else Assert.Equal("Stale", (await operation).Status);
        var session = (await store.GetAsync("session", default))!;
        var checkpoint = EllaiGuidanceCheckpoint.Deserialize(session.GuidanceJson)!;
        Assert.Null(checkpoint.Plan);
        if (providerFailure) Assert.Equal("Failed", checkpoint.Status);
        else Assert.Equal("Cancelled", session.Status);
    }

    private sealed class PlannerCompletion(Func<Task> beforeReturn) : IEllaiTurnCompletionClient
    {
        public async Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            var context = string.Join("\n", messages.Select(message => message.Content));
            Assert.Contains("report.utc-offset", context);
            Assert.Contains("report.other-observations", context);
            Assert.Contains("report.country-confirmed", context);
            await beforeReturn();
            return new("""
                {"schemaVersion":1,"status":"Ready","objective":"Create a report","mode":"Navigation",
                 "steps":[{"stepKey":"entry","controlId":"report.entry","objectiveId":"open-report-form","behavior":"Explain",
                 "uiIntentBindingId":null,"resourceBindingKey":null,"factIds":["public-intake-no-account"],"evidenceIds":[],
                 "explanation":"This is the report form.","nextStepKey":null}],"questions":[],"stopReason":null}
                """, 10, 5);
        }
    }

    [Theory]
    [InlineData("valid", "Completed")]
    [InlineData("proof", "EvidenceRequired")]
    [InlineData("submission", "EvidenceRequired")]
    [InlineData("old", "EvidenceRequired")]
    [InlineData("owner", "Denied")]
    [InlineData("version", "Conflict")]
    public async Task ReceiptCompletionRequiresOwnedCurrentMatchingDurableReceipt(string defect, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var descriptor = EllaiReportUiManifest.All.Single(control => control.ControlId == "report.submit");
        checkpoint = checkpoint with { StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), Plan = checkpoint.Plan! with { Steps =
            [checkpoint.Plan.Steps[0] with { ControlId = descriptor.ControlId, ObjectiveId = descriptor.ObjectiveId!, Behavior = PlanStepBehavior.GuideHuman }] } };
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, session.SessionVersion, default);
        var receipt = new IntakeReceipt(Guid.NewGuid(), Guid.NewGuid(), IntakeStatus.ReceivedUnverified,
            IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow.AddSeconds(-10));
        var source = new ReceiptAuthority(defect == "proof" ? null : receipt with
        {
            ClientSubmissionId = defect == "submission" ? Guid.NewGuid() : receipt.ClientSubmissionId,
            ReceivedAtUtc = defect == "old" ? DateTimeOffset.UtcNow.AddDays(-1) : receipt.ReceivedAtUtc,
        });
        var request = new EllaiGuidanceReceiptRequest(id, checkpoint.Plan.PlanId,
            defect == "version" ? 999 : checkpoint.Version, "step-1", receipt.ReceiptId, receipt.ClientSubmissionId);
        var authority = new EllaiUiEntitlements(defect == "owner" ? "foreign" : "owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var result = await service.CompleteReceiptStepAsync("session", request, new byte[32], authority, source, default);
        Assert.Equal(expected, result.Status);
        var saved = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        if (expected == "Completed")
        {
            Assert.Equal(receipt.ReceiptId, saved.IntakeEvidence!.ReceiptId);
            Assert.Equal("step-1", Assert.Single(saved.CompletedStepIds));
            var replay = await service.CompleteReceiptStepAsync("session", request, new byte[32], authority, source, default);
            Assert.Equal(result.Version, replay.Version);
            Assert.DoesNotContain("receiptProof", EllaiGuidanceCheckpoint.Serialize(saved), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(saved.IntakeEvidence);
            Assert.Empty(saved.CompletedStepIds);
        }
        if (defect is "owner" or "version") Assert.Equal(0, source.Reads);
    }

    private sealed class ReceiptAuthority(IntakeReceipt? receipt) : IIntakeSessionEndpointService
    {
        public int Reads { get; private set; }
        public Task<IntakeReceipt?> GetReceiptAsync(Guid receiptId, ReadOnlyMemory<byte> proof, CancellationToken ct)
        {
            Reads++;
            Assert.Equal(32, proof.Length);
            return Task.FromResult(receipt);
        }
        public Task<IntakeSessionResponse?> CreateAsync(Guid sessionId, ReadOnlyMemory<byte> proof, CancellationToken ct) => throw new NotSupportedException();
        public Task<IntakeCapability?> ResolveCapabilityAsync(string opaqueCapability, Guid sessionId, ReadOnlyMemory<byte> proof, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true, UiObservationState.Ready, "Completed")]
    [InlineData(false, UiObservationState.Ready, "EvidenceRequired")]
    [InlineData(true, UiObservationState.Hidden, "EvidenceRequired")]
    [InlineData(true, UiObservationState.Disabled, "EvidenceRequired")]
    [InlineData(true, UiObservationState.Unmounted, "EvidenceRequired")]
    public async Task ReportConfirmationOnlyAdvancesFromCurrentAcceptedFormState(bool accepted, UiObservationState state, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var descriptor = EllaiReportUiManifest.All.Single(control => control.ControlId == "report.confirmations");
        checkpoint = checkpoint with { Plan = checkpoint.Plan! with { Steps =
            [checkpoint.Plan.Steps[0] with { ControlId = descriptor.ControlId, ObjectiveId = descriptor.ObjectiveId!, Behavior = PlanStepBehavior.GuideHuman }] } };
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, session.SessionVersion, default);
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry", "report.confirmations"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready), new("report.confirmations", state)],
            CompletedControlIds: accepted ? ["report.confirmations"] : []), default);
        var result = await service.CompleteStepAsync("session", new(id, checkpoint.Plan.PlanId, checkpoint.Version, "step-1", "context"),
            new("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>()), default);
        Assert.Equal(expected, result.Status);
        Assert.Null(EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!.IntakeEvidence);
    }

    [Fact]
    public async Task VisibleReceiptCannotStandInForServerSubmissionEvidence()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var receipt = EllaiReportUiManifest.All.Single(control => control.ControlId == "report.receipt");
        checkpoint = checkpoint with { Plan = checkpoint.Plan! with { Steps =
            [checkpoint.Plan.Steps[0] with { ControlId = receipt.ControlId, ObjectiveId = receipt.ObjectiveId! }] } };
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, session.SessionVersion, default);
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry", "report.receipt"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready), new("report.receipt", UiObservationState.Ready)]), default);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var result = await service.CompleteStepAsync("session", new(id, checkpoint.Plan.PlanId, checkpoint.Version, "step-1", "context"), authority, default);
        Assert.Equal("EvidenceRequired", result.Status);
        Assert.Empty(EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!.CompletedStepIds);
    }

    [Fact]
    public async Task ExplainedStepCommitsOnceAndRestoresFromSessionStorage()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var request = new EllaiGuidanceStepRequest(id, checkpoint.Plan!.PlanId, checkpoint.Version, "step-1", "context");
        var first = await service.CompleteStepAsync("session", request, authority, default);
        Assert.Equal("Completed", first.Status);
        Assert.Equal(2, first.Version);
        var replay = await service.CompleteStepAsync("session", request, authority, default);
        Assert.Equal(first.Version, replay.Version);
        var restored = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Null(restored.CurrentStepId);
        Assert.Equal("step-1", Assert.Single(restored.CompletedStepIds));
        Assert.Equal("Completed", restored.Status);
    }

    [Theory]
    [InlineData("other", "context", 1, PlanStepBehavior.Explain, "Denied")]
    [InlineData("owner", "old-context", 1, PlanStepBehavior.Explain, "Stale")]
    [InlineData("owner", "context", 9, PlanStepBehavior.Explain, "Conflict")]
    [InlineData("owner", "context", 1, PlanStepBehavior.GuideHuman, "EvidenceRequired")]
    public async Task ProgressRejectsForeignStaleAndUnprovenCompletion(string owner, string context, long version,
        PlanStepBehavior behavior, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id, behavior);
        var result = await service.CompleteStepAsync("session", new(id, checkpoint.Plan!.PlanId, version, "step-1", context),
            new(owner, "scope", new HashSet<string> { "report" }, new HashSet<string>()), default);
        Assert.Equal(expected, result.Status);
        Assert.Empty(EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!.CompletedStepIds);
    }

    [Fact]
    public async Task HiddenTargetCannotCompleteAndNewTurnCannotReplayProgress()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var request = new EllaiGuidanceStepRequest(id, checkpoint.Plan!.PlanId, 1, "step-1", "context");
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Hidden)]), default);
        Assert.Equal("WaitingForControl", (await service.CompleteStepAsync("session", request, authority, default)).Status);
        var current = (await store.GetAsync("session", default))!;
        await store.TryAcceptUtteranceAsync(current with { SessionVersion = current.SessionVersion + 1 },
            current.SessionVersion, "newer-turn", default, "{}");
        Assert.Equal("Stale", (await service.CompleteStepAsync("session", request, authority, default)).Status);
    }

    [Theory]
    [InlineData(PlanStepBehavior.Explain, false, "Completed")]
    [InlineData(PlanStepBehavior.GuideHuman, false, "SkipNotAllowed")]
    [InlineData(PlanStepBehavior.Explain, true, "SkipNotAllowed")]
    public async Task SkipNeverCreatesCompletionEvidence(PlanStepBehavior behavior, bool mandatory, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id, behavior);
        if (mandatory)
        {
            checkpoint = checkpoint with { Plan = checkpoint.Plan! with
                { RemainingMandatoryObjectiveIds = [checkpoint.Plan.Steps[0].ObjectiveId] } };
            var current = (await store.GetAsync("session", default))!;
            await store.TryUpdateAsync(current with { SessionVersion = current.SessionVersion + 1,
                GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, current.SessionVersion, default);
        }
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var result = await service.ControlAsync("session", new(id, checkpoint.Plan!.PlanId, checkpoint.Version, "skip"), authority, default);
        Assert.Equal(expected, result.Status);
        Assert.Empty(result.CompletedStepIds!);
        var saved = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Empty(saved.CompletedStepIds);
        if (expected == "Completed")
        {
            Assert.Equal("step-1", Assert.Single(result.SkippedStepIds!));
            Assert.Equal("step-1", Assert.Single(saved.SkippedStepIds));
            Assert.Null(saved.CurrentStepId);
        }
        else Assert.Empty(saved.SkippedStepIds);
    }

    [Fact]
    public async Task BackAfterSkipRevisitsWithoutConvertingSkippedStepToCompleted()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var first = checkpoint.Plan!.Steps[0] with { NextStepKey = "second" };
        checkpoint = checkpoint with { Plan = checkpoint.Plan with { Steps = [first, first with { StepKey = "second", NextStepKey = null }] } };
        var current = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(current with { SessionVersion = current.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, current.SessionVersion, default);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var request = new EllaiGuidanceControlRequest(id, checkpoint.Plan.PlanId, checkpoint.Version, "skip");
        var skipped = await service.ControlAsync("session", request, authority, default);
        Assert.Equal("Ready", skipped.Status);
        Assert.Equal("second", skipped.CurrentStepId);
        Assert.Empty(skipped.CompletedStepIds!);
        Assert.Equal("Conflict", (await service.ControlAsync("session", request, authority, default)).Status);
        var back = await service.ControlAsync("session", request with { Command = "back", ExpectedVersion = skipped.Version }, authority, default);
        Assert.Equal("step-1", back.CurrentStepId);
        Assert.Empty(back.CompletedStepIds!);
        Assert.Empty(back.SkippedStepIds!);
    }

    [Fact]
    public async Task BackRewindsDurablePositionAndRejectsDuplicateVersion()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var first = checkpoint.Plan!.Steps[0] with { NextStepKey = "step-2" };
        var second = first with { StepKey = "step-2", NextStepKey = null };
        checkpoint = checkpoint with { Plan = checkpoint.Plan with { Steps = [first, second] },
            CurrentStepId = "step-2", CompletedStepIds = [first.StepKey] };
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, session.SessionVersion, default);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var request = new EllaiGuidanceControlRequest(id, checkpoint.Plan.PlanId, checkpoint.Version, "back");
        var result = await service.ControlAsync("session", request, authority, default);
        Assert.Equal("Ready", result.Status);
        Assert.Equal("step-1", result.CurrentStepId);
        Assert.Empty(result.CompletedStepIds!);
        Assert.Equal(checkpoint.Version + 1, result.Version);
        Assert.Equal("Conflict", (await service.ControlAsync("session", request, authority, default)).Status);
        var restored = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Equal("step-1", restored.CurrentStepId);
        Assert.Empty(restored.CompletedStepIds);
        Assert.Equal("AtStart", (await service.ControlAsync("session", request with { ExpectedVersion = result.Version }, authority, default)).Status);
    }

    [Fact]
    public async Task PauseResumeAndCancelAreDurableIdempotentAndCannotReopenTerminalPlan()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var pause = new EllaiGuidanceControlRequest(id, checkpoint.Plan!.PlanId, 1, "pause");
        var paused = await service.ControlAsync("session", pause, authority, default);
        Assert.Equal("Paused", paused.Status);
        Assert.Equal(2, paused.Version);
        Assert.Equal(2, (await service.ControlAsync("session", pause, authority, default)).Version);
        Assert.Equal("Conflict", (await service.CompleteStepAsync("session", new(id, checkpoint.Plan.PlanId, 2, "step-1", "context"), authority, default)).Status);
        var resumed = await service.ControlAsync("session", pause with { ExpectedVersion = 2, Command = "resume" }, authority, default);
        Assert.Equal("Ready", resumed.Status);
        Assert.Equal(3, resumed.Version);
        var cancelled = await service.ControlAsync("session", pause with { ExpectedVersion = 3, Command = "cancel" }, authority, default);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(4, cancelled.Version);
        Assert.Equal("Stale", (await service.ControlAsync("session", pause with { ExpectedVersion = 4, Command = "resume" }, authority, default)).Status);
        var restored = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!;
        Assert.Equal("Cancelled", restored.Status);
        Assert.Empty(restored.CompletedStepIds);
        Assert.Equal("Active", (await store.GetAsync("session", default))!.Status);
    }

    [Fact]
    public async Task CancelNeedsNoVisibleTargetButResumeRequiresFreshObservation()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var request = new EllaiGuidanceControlRequest(id, checkpoint.Plan!.PlanId, 1, "pause");
        Assert.Equal("Paused", (await service.ControlAsync("session", request, authority, default)).Status);
        await observations.RecordAsync(new("session", "view", "context", "web", [], DateTimeOffset.UtcNow, []), default);
        Assert.Equal("NeedsObservation", (await service.ControlAsync("session", request with { Command = "resume", ExpectedVersion = 2 }, authority, default)).Status);
        Assert.Equal("Cancelled", (await service.ControlAsync("session", request with { Command = "cancel", ExpectedVersion = 2 }, authority, default)).Status);
    }

    [Theory]
    [InlineData("other", "cancel", 1, "Denied")]
    [InlineData("owner", "complete", 1, "InvalidRequest")]
    [InlineData("owner", "cancel", 7, "Conflict")]
    public async Task LifecycleCannotBypassOwnerVersionOrCompletionEvidence(string owner, string command, long version, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var result = await service.ControlAsync("session", new(id, checkpoint.Plan!.PlanId, version, command),
            new(owner, "scope", new HashSet<string> { "report" }, new HashSet<string>()), default);
        Assert.Equal(expected, result.Status);
        Assert.Equal("Ready", EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("session", default))!.GuidanceJson)!.Status);
    }

    [Theory]
    [InlineData(false, "Ready")]
    [InlineData(true, "Stale")]
    public async Task AcceptedGuidanceSurvivesFollowupUnlessANewGuidanceRequestSupersedesIt(bool replacement, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var current = (await store.GetAsync("session", default))!;
        await store.TryAcceptUtteranceAsync(current with { SessionVersion = current.SessionVersion + 1,
            InteractionEpoch = current.InteractionEpoch + 1 }, current.SessionVersion, "followup", default, "{}");
        var claim = (await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default))!;
        await store.CompleteAsync("session", "followup", claim.AttemptCount, DateTimeOffset.UtcNow, default,
            EllaiUtteranceResolution.Completed("Here is an explanation.", current.InteractionEpoch + 1) with
            {
                UiProposalsJson = replacement ? EllaiUiProposalPayload.Serialize([new(UiIntentKind.StartGuidance, "ellai.guidance", null, null,
                    new Dictionary<string, string> { ["feature"] = "report", ["objective"] = "A different tour" })]) : null,
            });
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        Assert.Equal(expected, (await service.AdmitAsync("session", id, authority, default)).Status);
        if (replacement)
        {
            var after = (await store.GetAsync("session", default))!;
            await store.TryAcceptUtteranceAsync(after with { SessionVersion = after.SessionVersion + 1,
                InteractionEpoch = after.InteractionEpoch + 1 }, after.SessionVersion, "later-chat", default, "{}");
            var later = (await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default))!;
            await store.CompleteAsync("session", "later-chat", later.AttemptCount, DateTimeOffset.UtcNow, default,
                EllaiUtteranceResolution.Completed("An ordinary reply", after.InteractionEpoch + 1));
            Assert.Equal("Stale", (await service.AdmitAsync("session", id, authority, default)).Status);
        }
        if (!replacement)
        {
            var paused = await service.ControlAsync("session", new(id, checkpoint.Plan!.PlanId, 1, "pause"), authority, default);
            Assert.Equal("Paused", paused.Status);
            var resumed = await service.ControlAsync("session", new(id, checkpoint.Plan.PlanId, paused.Version, "resume"), authority, default);
            Assert.Equal("Ready", resumed.Status);
            Assert.Equal("Completed", (await service.CompleteStepAsync("session",
                new(id, checkpoint.Plan.PlanId, resumed.Version, "step-1", "context"), authority, default)).Status);
        }
    }

    [Theory]
    [InlineData(false, "Ready")]
    [InlineData(true, "Stale")]
    public async Task GuidanceSurvivesHistoryWindowWithoutRevivingSupersededPlans(bool replacement, string expected)
    {
        var (service, store, observations, id) = await Fixture();
        await SeedProgress(store, observations, id);
        for (var index = 0; index < 105; index++)
        {
            var current = (await store.GetAsync("session", default))!;
            var turnId = $"followup-{index}";
            await store.TryAcceptUtteranceAsync(current with { SessionVersion = current.SessionVersion + 1,
                InteractionEpoch = current.InteractionEpoch + 1 }, current.SessionVersion, turnId, default, "{}");
            var claim = (await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default))!;
            await store.CompleteAsync("session", turnId, claim.AttemptCount, DateTimeOffset.UtcNow, default,
                EllaiUtteranceResolution.Completed("An ordinary reply", current.InteractionEpoch + 1) with
                {
                    UiProposalsJson = replacement && index == 0 ? EllaiUiProposalPayload.Serialize([
                        new(UiIntentKind.StartGuidance, "ellai.guidance", null, null,
                            new Dictionary<string, string> { ["feature"] = "report", ["objective"] = "A replacement tour" })]) : null,
                });
        }
        Assert.Equal(100, (await store.ReadConversationAsync("session", "owner", default)).Count);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        Assert.Equal(expected, (await service.AdmitAsync("session", id, authority, default)).Status);
    }

    [Fact]
    public async Task ValidationNarrationUsesTheFirstObservedTrustedFieldAndRejectsStaleOrInventedIssues()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        await observations.RecordAsync(new("session", "view", "validation", "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready)], ValidationInputIds: ["report.original-statement", "report.occurrence-date"]), default);
        var responses = new EllaiSessionResponseStore();
        var request = new EllaiGuidanceNarrationRequest(id, checkpoint.Plan!.PlanId, checkpoint.CurrentStepId!, checkpoint.Version)
            { ValidationInputId = "report.original-statement", ObservationContextStamp = "validation" };

        Assert.Null(await service.PrepareNarrationAsync("session", request with { ObservationContextStamp = "old" }, authority, responses, default));
        Assert.Null(await service.PrepareNarrationAsync("session", request with { ValidationInputId = "report.occurrence-date" }, authority, responses, default));
        Assert.Null(await service.PrepareNarrationAsync("session", request with { ValidationInputId = "malicious.input" }, authority, responses, default));
        var response = await service.PrepareNarrationAsync("session", request, authority, responses, default);
        Assert.Equal(EllaiReportValidationGuidance.Find("report.original-statement")!.Message, response!.Text);
        Assert.True(response.Progress!.Speak);
        await observations.RecordAsync(new("session", "view", "corrected", "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready)], ValidationInputIds: ["report.occurrence-date"]), default);
        Assert.Null(await service.PrepareNarrationAsync("session", request, authority, responses, default));
        var next = await service.PrepareNarrationAsync("session", request with
            { ValidationInputId = "report.occurrence-date", ObservationContextStamp = "corrected" }, authority, responses, default);
        Assert.Equal(EllaiReportValidationGuidance.Find("report.occurrence-date")!.Message, next!.Text);
    }

    [Fact]
    public async Task NarrationUsesOnlyTheCurrentOwnedPersistedStepAndDeduplicates()
    {
        var (service, store, observations, id) = await Fixture();
        var checkpoint = await SeedProgress(store, observations, id);
        var authority = new EllaiUiEntitlements("owner", "scope", new HashSet<string> { "report" }, new HashSet<string>());
        var responses = new EllaiSessionResponseStore();
        var request = new EllaiGuidanceNarrationRequest(id, checkpoint.Plan!.PlanId, checkpoint.CurrentStepId!, checkpoint.Version, "am_michael");
        Assert.Null(await service.PrepareNarrationAsync("session", request, authority with { OwnerActorId = "other" }, responses, default));
        Assert.Null(await service.PrepareNarrationAsync("session", request with { ExpectedVersion = checkpoint.Version + 1 }, authority, responses, default));
        Assert.Null(await service.PrepareNarrationAsync("session", request with { StepId = "not-current" }, authority, responses, default));
        Assert.Null(await service.PrepareNarrationAsync("session", request with { EnglishVoiceId = "untrusted" }, authority, responses, default));
        var first = await service.PrepareNarrationAsync("session", request, authority, responses, default);
        Assert.NotNull(first);
        Assert.Equal(checkpoint.Plan.Steps[0].Explanation, first.Text);
        Assert.Equal("turn", first.ClientUtteranceId);
        Assert.Equal("am_michael", first.EnglishVoiceId);
        Assert.Equal("Guidance", first.Progress?.State);
        Assert.False(first.IsFinal);
        Assert.Equal(first.Sequence, (await service.PrepareNarrationAsync("session", request, authority, responses, default))!.Sequence);
        Assert.Single(responses.ReadSince("session", 0).Responses);
        Assert.True(responses.CanSpeakProgress(first));
        responses.InterruptProgress("session", first.Sequence);
        Assert.False(responses.CanSpeakProgress(first));
        var session = (await store.GetAsync("session", default))!;
        await store.TryUpdateAsync(session with { SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint with { Status = "Paused" }) }, session.SessionVersion, default);
        Assert.Null(await service.PrepareNarrationAsync("session", request, authority, responses, default));
    }

    [Fact]
    public void NewGuidanceCanSpeakAfterAnEarlierStepWasInterruptedButSupersedesItsAudio()
    {
        var responses = new EllaiSessionResponseStore();
        var first = responses.PublishProgress("session", "turn", "First step", new("g1", "Guidance", "Guidance", true, 1, DateTimeOffset.UtcNow.AddMinutes(1)))!;
        responses.InterruptProgress("session", first.Sequence);
        var next = responses.PublishProgress("session", "turn", "Next step", new("g2", "Guidance", "Guidance", true, 1, DateTimeOffset.UtcNow.AddMinutes(1)))!;
        Assert.False(responses.CanSpeakProgress(first));
        Assert.True(responses.CanSpeakProgress(next));
        responses.PublishChunk("session", "new-turn", 1, "A newer answer");
        Assert.False(responses.CanSpeakProgress(next));
    }

    private static async Task<EllaiGuidanceCheckpoint> SeedProgress(InMemoryEllaiSessionStore store,
        InMemoryEllaiUiObservationStore observations, string proposalId, PlanStepBehavior behavior = PlanStepBehavior.Explain)
    {
        var current = (await store.GetAsync("session", default))!;
        var checkpoint = EllaiGuidanceCheckpointTests.Sample() with { ProposalId = proposalId, InteractionEpoch = current.InteractionEpoch };
        checkpoint = checkpoint with { Plan = checkpoint.Plan! with { Steps = [checkpoint.Plan.Steps[0] with { Behavior = behavior }] } };
        await store.TryUpdateAsync(current with { SessionVersion = current.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint) }, current.SessionVersion, default);
        await observations.RecordAsync(new("session", "view", "context", "web", ["report.entry"], DateTimeOffset.UtcNow,
            [new("report.entry", UiObservationState.Ready)]), default);
        return checkpoint;
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
