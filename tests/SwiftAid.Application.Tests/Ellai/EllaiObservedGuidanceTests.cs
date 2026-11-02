using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiObservedGuidanceTests
{
    [Fact]
    public void CapturePlanCarriesAllInputsForItsSectionsIncludingConditionalFields()
    {
        var (registry, _, _) = Create();
        var ids = new[] { "report.subject", "report.narrative", "report.time-location", "report.confirmations" };
        var steps = ids.Select((id, index) =>
        {
            var control = EllaiReportUiManifest.All.Single(item => item.ControlId == id);
            return new GuidancePlanStep("s" + index, id, control.ObjectiveId!, PlanStepBehavior.GuideHuman,
                null, null, [], [], "Complete the applicable section inputs.", index + 1 < ids.Length ? "s" + (index + 1) : null);
        }).ToArray();
        var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, "Capture report", GuidanceMode.Navigation, steps, [], null);
        var result = new EllaiGuidancePlanValidator(registry).Validate(draft, new("report", [], 20),
            new HashSet<string> { "report" }, new HashSet<string>(), "plan", 1, 0, 0);
        Assert.NotNull(result.Plan);
        var fields = result.Plan.InputsByStep.Values.SelectMany(inputs => inputs).ToArray();
        Assert.Equal(17, fields.Length);
        Assert.Equal(fields.Length, fields.Select(field => field.InputId).Distinct().Count());
        Assert.Contains(fields, field => field.InputId == "report.animal-scope" && field.Required && field.ApplicableWhen.Contains("Animal"));
        Assert.Contains(fields, field => field.InputId == "report.utc-offset" && field.Required && field.ApplicableWhen.Contains("Exact"));
        Assert.Contains(fields, field => field.InputId == "report.other-observations" && !field.Required);
        Assert.Equal(3, result.Plan.InputsByStep["s3"].Count);
        Assert.Equal(steps.Length, result.Plan.Steps.Count); // Inventory does not invent extra steps or validation gates.
    }

    [Theory]
    [InlineData("report.entry", "open-report-form", "alternative", true)]
    [InlineData("unregistered", "open-report-form", "alternative", false)]
    [InlineData("report.entry", "invented", "alternative", false)]
    [InlineData("report.entry", "open-report-form", "missing", false)]
    public void SuggestionsResolveThroughSharedDictionaryAndAuthorizedRegistry(string controlId, string objectiveId, string reference, bool valid)
    {
        var (registry, _, _) = Create();
        var step = new GuidancePlanStep("s1", "report.entry", "open-report-form", PlanStepBehavior.Explain,
            null, null, [], [], "Read the heading.", null) { SuggestionIds = [reference] };
        var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, "Tour", GuidanceMode.Navigation, [step], [], null)
        {
            Suggestions = new Dictionary<string, GuidanceStepSuggestion>
            {
                ["alternative"] = new("Explain the form", controlId, objectiveId)
            }
        };
        var result = new EllaiGuidancePlanValidator(registry).Validate(draft, new("report", [], 20),
            new HashSet<string> { "report" }, new HashSet<string>(), "plan", 1, 0, 0);
        Assert.Equal(valid, result.Plan is not null);
        if (valid) Assert.Equal(draft.Suggestions, result.Plan!.Suggestions);
    }

    [Fact]
    public void HumanGuidanceForReadOnlyHeadingsUsesObservedPresentationWithoutWeakeningWrites()
    {
        var (registry, _, _) = Create();
        var ids = new[] { "report.entry", "report.subject", "report.narrative", "report.time-location", "report.review", "report.confirmations", "report.submit", "report.receipt" };
        var descriptors = ids.Select(id => EllaiReportUiManifest.All.Single(control => control.ControlId == id)).ToArray();
        var steps = descriptors.Select((control, index) => new GuidancePlanStep("s" + index, control.ControlId,
            control.ObjectiveId!, PlanStepBehavior.GuideHuman, null, null, [], [], "Use the report workflow.",
            index + 1 < ids.Length ? "s" + (index + 1) : null)).ToArray();
        var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, "Submit report", GuidanceMode.Navigation, steps, [], null);
        var result = new EllaiGuidancePlanValidator(registry).Validate(draft, new("report", [], 20),
            new HashSet<string> { "report" }, new HashSet<string>(), "plan", 1, 0, 0);
        Assert.NotNull(result.Plan);
        foreach (var step in result.Plan.Steps)
            Assert.Equal(step.ControlId is "report.entry" or "report.review" ? PlanStepBehavior.Explain : PlanStepBehavior.GuideHuman, step.Behavior);
    }

    [Fact]
    public void DraftSaveCanBePlannedAfterSubjectButCannotStartDisabled()
    {
        var (registry, _, _) = Create();
        var descriptors = new[] { "report.subject", "report.save" }
            .Select(id => EllaiReportUiManifest.All.Single(control => control.ControlId == id)).ToArray();
        var steps = descriptors.Select((control, index) => new GuidancePlanStep("s" + index,
            control.ControlId, control.ObjectiveId!, PlanStepBehavior.GuideHuman,
            null, null, [], [], "Use the form control.", index == 0 ? "s1" : null)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var observations = descriptors.Select((control, index) => new UiControlObservation(control.ControlId,
            control.DescriptorVersion, "view", "context", index == 0 ? UiObservationState.Ready : UiObservationState.Disabled,
            "web", now, null)).ToArray();
        var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, "Save draft", GuidanceMode.Navigation, steps, [], null);
        var validator = new EllaiGuidancePlanValidator(registry);
        Assert.NotNull(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), observations, "view", "context", now, "plan").Plan);
        Assert.Null(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), observations, "view", "context", now, "plan", "s1", new HashSet<string> { "s0" }).Plan);
        observations[1] = observations[1] with { State = UiObservationState.Ready };
        Assert.NotNull(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), observations, "view", "context", now, "plan", "s1", new HashSet<string> { "s0" }).Plan);
    }

    [Fact]
    public void LaterWizardTargetsCanBePlannedAfterTheirRegisteredPrerequisites()
    {
        var (registry, _, _) = Create();
        var ids = new[] { "report.subject", "report.narrative", "report.time-location", "report.review" };
        var descriptors = ids.Select(id => EllaiReportUiManifest.All.Single(control => control.ControlId == id)).ToArray();
        var steps = descriptors.Select((control, index) => new GuidancePlanStep("s" + index, control.ControlId, control.ObjectiveId!,
            control.ProofPolicyId == "proof-ui-validated" ? PlanStepBehavior.GuideHuman : PlanStepBehavior.Explain,
            null, null, [], [], "Complete this form step.", index + 1 < ids.Length ? "s" + (index + 1) : null)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var observed = descriptors.Select((control, index) => new UiControlObservation(control.ControlId, 1, "view", "context",
            index == 0 ? UiObservationState.Ready : UiObservationState.Unmounted, "web", now, null)).ToArray();
        var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, "Capture a report", GuidanceMode.Navigation, steps, [], null);
        var validator = new EllaiGuidancePlanValidator(registry);
        Assert.NotNull(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), observed, "view", "context", now, "plan").Plan);
        var restored = observed.ToArray();
        restored[0] = restored[0] with { State = UiObservationState.Unmounted };
        restored[1] = restored[1] with { State = UiObservationState.Ready };
        Assert.NotNull(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), restored, "view", "context", now, "plan", "s1", new HashSet<string> { "s0" }).Plan);
        Assert.Null(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), restored, "view", "context", now, "plan", "s1", new HashSet<string>()).Plan);
        restored[1] = restored[1] with { State = UiObservationState.Unmounted };
        Assert.NotNull(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), restored, "view", "context", now, "plan", "s1", new HashSet<string> { "s0" },
            new HashSet<string> { "report.narrative" }).Plan);
        restored[1] = restored[1] with { State = UiObservationState.Hidden };
        Assert.Null(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), restored, "view", "context", now, "plan", "s1", new HashSet<string> { "s0" },
            new HashSet<string> { "report.narrative" }).Plan);
        // The active target must still be visible; future targets cannot bypass a hidden or disabled control.
        foreach (var state in new[] { UiObservationState.Hidden, UiObservationState.Disabled, UiObservationState.Ambiguous })
        {
            var changed = observed.ToArray();
            changed[1] = changed[1] with { State = state };
            Assert.Null(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
                new HashSet<string>(), changed, "view", "context", now, "plan").Plan);
        }
    }

    [Fact]
    public void FullReportPlanCanGuideHumanThroughConditionalConfirmationSubmissionAndReceipt()
    {
        var (registry, _, _) = Create();
        var ids = new[] { "report.subject", "report.narrative", "report.time-location", "report.review", "report.confirmations", "report.submit", "report.receipt" };
        var descriptors = ids.Select(id => EllaiReportUiManifest.All.Single(control => control.ControlId == id)).ToArray();
        var steps = descriptors.Select((control, index) => new GuidancePlanStep("s" + index, control.ControlId, control.ObjectiveId!,
            control.HumanConfirmationRequired || control.ProofPolicyId == "proof-ui-validated" ? PlanStepBehavior.GuideHuman : PlanStepBehavior.Explain,
            null, null, [], [], "Use the existing report workflow.", index + 1 < ids.Length ? "s" + (index + 1) : null)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var observed = descriptors.Select((control, index) => new UiControlObservation(control.ControlId, 1, "view", "context",
            index == 0 ? UiObservationState.Ready : UiObservationState.Unmounted, "web", now, null)).ToArray();
        var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, "Submit a report", GuidanceMode.Navigation, steps, [], null);
        var validator = new EllaiGuidancePlanValidator(registry);
        Assert.NotNull(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), observed, "view", "context", now, "plan").Plan);
        var unsafeSteps = steps.ToArray();
        unsafeSteps[5] = unsafeSteps[5] with { Behavior = PlanStepBehavior.RequestUiIntent, UiIntentBindingId = "Submit" };
        Assert.Null(validator.ValidateObserved(draft with { Steps = unsafeSteps }, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), observed, "view", "context", now, "plan").Plan);
    }

    [Fact]
    public void RestoredOptionalSkipIsSeparateFromCompletionAndCannotBeCountedTwice()
    {
        var (registry, _, _) = Create();
        var descriptor = EllaiReportUiManifest.All.Single(control => control.ControlId == "report.entry");
        var first = new GuidancePlanStep("first", descriptor.ControlId, descriptor.ObjectiveId!, PlanStepBehavior.Explain,
            null, null, [], [], "Explain the form.", "second");
        var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, "Explore reporting", GuidanceMode.Navigation,
            [first, first with { StepKey = "second", NextStepKey = null }], [], null);
        var now = DateTimeOffset.UtcNow;
        UiControlObservation[] observed = [new(descriptor.ControlId, 1, "view", "context", UiObservationState.Ready, "web", now, null)];
        var validator = new EllaiGuidancePlanValidator(registry);
        Assert.NotNull(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" }, new HashSet<string>(),
            observed, "view", "context", now, "plan", "second", new HashSet<string>(), null, new HashSet<string> { "first" }).Plan);
        Assert.Null(validator.ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" }, new HashSet<string>(),
            observed, "view", "context", now, "plan", "second", new HashSet<string> { "first" }, null, new HashSet<string> { "first" }).Plan);
    }

    [Fact]
    public void LegacyMountedOnlyReportsDoNotProveReadiness()
    {
        var (registry, _, _) = Create();
        var snapshot = registry.GetSnapshot(new("report", [], 20), new HashSet<string> { "report" }, new HashSet<string>());
        var observations = new EllaiUiDiscoveryService().Observe(snapshot, new HashSet<string> { "report.entry" },
            "view", "context", "web", DateTimeOffset.UtcNow);
        Assert.Equal(UiObservationState.Unknown, observations.Single(o => o.ControlId == "report.entry").State);
        Assert.True(new UiObservationReport("view", "context", "web", []).IsValid());
        Assert.False(new UiObservationReport("view", "context", "web", ["report.entry"],
            [new("report.entry", UiObservationState.Ready), new("report.entry", UiObservationState.Hidden)]).IsValid());
    }

    [Theory]
    [InlineData(UiObservationState.Hidden, 0)]
    [InlineData(UiObservationState.Disabled, 0)]
    [InlineData(UiObservationState.Ready, 46)]
    public void HiddenDisabledAndStaleTargetsCannotBecomeAcceptedPlans(UiObservationState state, int age)
    {
        var (registry, draft, observation) = Create();
        var result = Validate(registry, draft, observation with { State = state, ObservedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-age) });
        Assert.Null(result.Plan);
    }

    [Fact]
    public void InventedFactsAndObjectivesFailAdmission()
    {
        var (registry, draft, observation) = Create();
        Assert.NotNull(Validate(registry, draft, observation).Plan);
        Assert.Null(Validate(registry, draft with { Steps = [draft.Steps[0] with { FactIds = ["invented"] }] }, observation).Plan);
        Assert.Null(Validate(registry, draft with { Steps = [draft.Steps[0] with { ObjectiveId = "submit-without-review" }] }, observation).Plan);
    }

    [Fact]
    public void CyclicAndDisconnectedPlansFailStructuralValidation()
    {
        var (_, draft, _) = Create();
        var first = draft.Steps[0] with { NextStepKey = "second" };
        var second = first with { StepKey = "second", NextStepKey = first.StepKey };
        Assert.NotEmpty(EllaiDecisionValidator.ValidateDraft(draft with { Steps = [first, second] }));
        Assert.NotEmpty(EllaiDecisionValidator.ValidateDraft(draft with { Steps = [first with { NextStepKey = null }, second with { NextStepKey = null }] }));
    }

    [Theory]
    [InlineData(true, UiObservationState.Ready, true)]
    [InlineData(false, UiObservationState.Ready, false)]
    [InlineData(true, UiObservationState.Unmounted, false)]
    public void DashboardTourRequiresAuthorityAndObservedTargets(bool entitled, UiObservationState state, bool accepted)
    {
        var registry = new EllaiUiRegistry();
        foreach (var control in EllaiDashboardUiManifest.Controls) registry.Register(control);
        var controls = EllaiDashboardUiManifest.Controls;
        var now = DateTimeOffset.UtcNow;
        var steps = controls.Select((control, index) => new GuidancePlanStep("s" + index, control.ControlId,
            control.ObjectiveId!, PlanStepBehavior.Explain, null, null, [], [], control.DescriptionResourceKey,
            index + 1 < controls.Count ? "s" + (index + 1) : null)).ToArray();
        var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, "Explain dashboard filters", GuidanceMode.SystemTour, steps, [], null);
        var observations = controls.Select(control => new UiControlObservation(control.ControlId, control.DescriptorVersion,
            "view", "context", state, "web", now, null)).ToArray();
        var result = new EllaiGuidancePlanValidator(registry).ValidateObserved(draft, new("dashboard", [], 20),
            new HashSet<string> { "dashboard" }, entitled ? new HashSet<string> { EllaiNavigationManifest.Capability } : new HashSet<string>(),
            observations, "view", "context", now, "dashboard-plan");
        Assert.Equal(accepted, result.Plan is not null);
    }

    private static (EllaiUiRegistry, EllaiGuidancePlanDraft, UiControlObservation) Create()
    {
        var registry = new EllaiUiRegistry();
        foreach (var descriptor in EllaiReportGuidanceDescriptors.All) registry.Register(descriptor);
        var entry = EllaiReportGuidanceDescriptors.All[0];
        return (registry, new(1, GuidancePlanStatus.Ready, "Explain reporting", GuidanceMode.Navigation,
            [new("step", entry.ControlId, entry.ObjectiveId!, PlanStepBehavior.Explain, null, null,
                entry.ProductFactIds, [], "Open the report form.", null)], [], null),
            new(entry.ControlId, entry.DescriptorVersion, "view", "context", UiObservationState.Ready, "web", DateTimeOffset.UtcNow, null));
    }

    private static (ValidatedGuidancePlan? Plan, IReadOnlyList<string> Errors) Validate(EllaiUiRegistry registry, EllaiGuidancePlanDraft draft, UiControlObservation observation) =>
        new EllaiGuidancePlanValidator(registry).ValidateObserved(draft, new("report", [], 20), new HashSet<string> { "report" },
            new HashSet<string>(), [observation], "view", "context", DateTimeOffset.UtcNow, "plan");
}
