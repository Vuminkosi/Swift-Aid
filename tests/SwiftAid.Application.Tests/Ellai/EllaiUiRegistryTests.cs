using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T33")]
public sealed class EllaiUiRegistryTests
{
    private static UiControlDescriptor Descriptor(string control, string feature = "cases") => new(
        control, feature, 1, "ellai-uicontract/1.0", "Cases",
        "Button", "label-key", "desc-key",
        Array.Empty<string>(), new[] { "view-queue" }, Array.Empty<string>(),
        "nav-cases", "anchor-queue",
        new[] { "HighlightControl" }, UiRiskClass.R0,
        new[] { GuidanceMode.Navigation }, Array.Empty<string>(), Array.Empty<string>(),
        "view-queue", "proof-ui-observed",
        new[] { "cases.read" }, "internal", false);

    [Fact]
    public void ProjectionIsBoundedAndPermissionFiltered()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));
        registry.Register(Descriptor("billing.refunds", "billing"));

        var snapshot = registry.GetSnapshot(
            new UiRegistryQuery("cases", new[] { "view-queue" }, 20),
            new HashSet<string> { "cases" }, new HashSet<string> { "cases.read" });

        Assert.Single(snapshot.Descriptors);
        Assert.Equal("cases.queue", snapshot.Descriptors[0].ControlId);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public void UnentitledCapabilityIsExcluded()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));

        var snapshot = registry.GetSnapshot(
            new UiRegistryQuery("cases", Array.Empty<string>(), 20),
            new HashSet<string> { "cases" }, new HashSet<string>());

        Assert.Empty(snapshot.Descriptors);
    }

    [Fact]
    public void ValidatorRejectsUnknownAndProtectedTargets()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));

        var validator = new EllaiGuidancePlanValidator(registry);
        var draft = new EllaiGuidancePlanDraft(
            1, GuidancePlanStatus.Ready, "goal", GuidanceMode.Navigation,
            new[]
            {
                new GuidancePlanStep("s1", "cases.queue", "o1", PlanStepBehavior.Explain,
                    null, null, Array.Empty<string>(), Array.Empty<string>(), "ok", "s2"),
                new GuidancePlanStep("s2", "nonexistent.control", "o2", PlanStepBehavior.Explain,
                    null, null, Array.Empty<string>(), Array.Empty<string>(), "ok", null),
            },
            Array.Empty<EllaiQuestion>(), null);

        var (plan, errors) = validator.Validate(
            draft, new UiRegistryQuery("cases", Array.Empty<string>(), 20),
            new HashSet<string> { "cases" }, new HashSet<string> { "cases.read" },
            "plan-1", 1, 0, 0);

        Assert.Null(plan);
        Assert.Contains(errors, e => e.Contains("nonexistent.control", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatorAcceptsEligiblePlan()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));

        var validator = new EllaiGuidancePlanValidator(registry);
        var draft = new EllaiGuidancePlanDraft(
            1, GuidancePlanStatus.Ready, "goal", GuidanceMode.Navigation,
            new[]
            {
                new GuidancePlanStep("s1", "cases.queue", "view-queue", PlanStepBehavior.RequestUiIntent,
                    "HighlightControl", null, Array.Empty<string>(), Array.Empty<string>(), "See the queue.", null),
            },
            Array.Empty<EllaiQuestion>(), null);

        var (plan, errors) = validator.Validate(
            draft, new UiRegistryQuery("cases", Array.Empty<string>(), 20),
            new HashSet<string> { "cases" }, new HashSet<string> { "cases.read" },
            "plan-1", 1, 0, 0);

        Assert.Empty(errors);
        Assert.NotNull(plan);
        Assert.Equal("plan-1", plan.PlanId);
    }
}
