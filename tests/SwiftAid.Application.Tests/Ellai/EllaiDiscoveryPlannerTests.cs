using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T35")]
public sealed class EllaiDiscoveryPlannerTests
{
    private static UiControlDescriptor Descriptor(string control, UiRiskClass risk = UiRiskClass.R0) => new(
        control, "cases", 1, "ellai-uicontract/1.0", "Cases",
        "Button", "label-key", "desc-key",
        Array.Empty<string>(), new[] { "view-queue" }, Array.Empty<string>(),
        "nav-cases", "anchor-queue",
        new[] { "HighlightControl" }, risk,
        new[] { GuidanceMode.Navigation }, Array.Empty<string>(), Array.Empty<string>(),
        "view-queue", "proof-ui-observed",
        new[] { "cases.read" }, "internal", false);

    private static UiRegistrySnapshot Snapshot(EllaiUiRegistry registry) =>
        registry.GetSnapshot(
            new UiRegistryQuery("cases", Array.Empty<string>(), 20),
            new HashSet<string> { "cases" }, new HashSet<string> { "cases.read" });

    [Fact]
    public void DiscoveryMarksOnlyMountedControlsReady()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));
        registry.Register(Descriptor("cases.filter"));
        var discovery = new EllaiUiDiscoveryService();

        var observations = discovery.Observe(
            Snapshot(registry), new HashSet<string> { "cases.queue" },
            "view-1", "ctx-1", "web", DateTimeOffset.UtcNow, [new("cases.queue", UiObservationState.Ready), new("cases.refund", UiObservationState.Ready)]);

        Assert.Equal(2, observations.Count);
        Assert.Equal(UiObservationState.Ready,
            observations.Single(o => o.ControlId == "cases.queue").State);
        Assert.Equal(UiObservationState.Unmounted,
            observations.Single(o => o.ControlId == "cases.filter").State);
    }

    [Fact]
    public void PlannerComposesReadyControlsAndSkipsProtectedOnes()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));
        registry.Register(Descriptor("cases.refund", UiRiskClass.R3));
        var snapshot = Snapshot(registry);
        var discovery = new EllaiUiDiscoveryService();
        var observations = discovery.Observe(
            snapshot, new HashSet<string> { "cases.queue", "cases.refund" },
            "view-1", "ctx-1", "web", DateTimeOffset.UtcNow, [new("cases.queue", UiObservationState.Ready), new("cases.refund", UiObservationState.Ready)]);
        var planner = new EllaiGuidancePlanner();

        var draft = planner.ComposeDraft("view queue", GuidanceMode.Navigation, snapshot, observations, 8);

        Assert.Equal(GuidancePlanStatus.Ready, draft.Status);
        var single = Assert.Single(draft.Steps);
        Assert.Equal("cases.queue", single.ControlId);
    }

    [Fact]
    public void PlannerAsksInsteadOfFabricatingWhenNothingReady()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));
        var snapshot = Snapshot(registry);
        var observations = new EllaiUiDiscoveryService().Observe(
            snapshot, new HashSet<string>(), "view-1", "ctx-1", "web", DateTimeOffset.UtcNow, [new("cases.queue", UiObservationState.Ready), new("cases.refund", UiObservationState.Ready)]);
        var planner = new EllaiGuidancePlanner();

        var draft = planner.ComposeDraft("view queue", GuidanceMode.Navigation, snapshot, observations, 8);

        Assert.Equal(GuidancePlanStatus.NeedsInput, draft.Status);
        Assert.Empty(draft.Steps);
        Assert.Single(draft.Questions);
    }

    [Fact]
    public void ValidatorAcceptsPlannerDraftClosingTheLoop()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));
        var snapshot = Snapshot(registry);
        var observations = new EllaiUiDiscoveryService().Observe(
            snapshot, new HashSet<string> { "cases.queue" },
            "view-1", "ctx-1", "web", DateTimeOffset.UtcNow, [new("cases.queue", UiObservationState.Ready), new("cases.refund", UiObservationState.Ready)]);
        var draft = new EllaiGuidancePlanner().ComposeDraft(
            "view queue", GuidanceMode.Navigation, snapshot, observations, 8);

        var (plan, errors) = new EllaiGuidancePlanValidator(registry).Validate(
            draft, new UiRegistryQuery("cases", Array.Empty<string>(), 20),
            new HashSet<string> { "cases" }, new HashSet<string> { "cases.read" },
            "plan-1", 1, 0, 0);

        Assert.Empty(errors);
        Assert.NotNull(plan);
        Assert.Single(plan.Steps);
    }
}
