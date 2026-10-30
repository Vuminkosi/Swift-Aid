using Bit.Bonboarding;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T36")]
public sealed class EllaiCompilerInteropTests
{
    private static ValidatedGuidancePlan Plan() => new(
        "plan-1", 1, null, "Initial acceptance",
        new[] { "s1", "s2" }, Array.Empty<string>(), Array.Empty<string>(),
        new[]
        {
            new GuidancePlanStep("s1", "cases.queue", "view-queue", PlanStepBehavior.Explain,
                null, null, Array.Empty<string>(), Array.Empty<string>(), "See the queue.", "s2"),
            new GuidancePlanStep("s2", "cases.queue", "open-first", PlanStepBehavior.GuideHuman,
                null, null, Array.Empty<string>(), Array.Empty<string>(), "Open the first item.", null),
        },
        Array.Empty<string>(), "ellai-catalogue/1.0", "ellai-uicontract/1.0");

    [Fact]
    public void AcceptedPlanCompilesToRealTourDefinition()
    {
        var compiler = new EllaiBonboardingCompiler();
        var definition = compiler.Compile(
            Plan(), new Dictionary<string, string> { ["cases.queue"] = "anchor-cases-queue" });

        Assert.StartsWith("ellai-generated-plan-1-r1", definition.Id, StringComparison.Ordinal);
        Assert.Equal(2, definition.Steps.Count);
        Assert.False(definition.Steps[0].Target.IsNone);
        Assert.True(definition.Steps[0].Target.IsDynamic is false);
    }

    [Fact]
    public void UnmappedControlFallsBackToNone()
    {
        var compiler = new EllaiBonboardingCompiler();
        var definition = compiler.Compile(Plan(), new Dictionary<string, string>());

        Assert.All(definition.Steps, step => Assert.True(step.Target.IsNone));
    }

    [Fact]
    public void CuratedManifestCompilesAlongTheSamePipeline()
    {
        var compiler = new EllaiBonboardingCompiler();
        var manifest = new EllaiJourneyManifest(
            1, "curated-queue-tour-001", "1.0", "fixture-static",
            new[] { GuidanceMode.SystemTour }, "synthetic",
            new[] { "cases.read" }, new[] { "productfacts/1.0" },
            new[]
            {
                new EllaiGuidanceStep("see-queue", "queue-overview", "cases", "cases.queue",
                    null, Array.Empty<string>(), null, "narr", "policy",
                    GuidanceAdvancePolicy.UiObservationRequired, "proof-ui-observed",
                    true, Array.Empty<string>(), null),
            },
            new[] { "view-queue" }, false, null);

        var definition = compiler.CompileManifest(
            manifest, new Dictionary<string, string> { ["cases.queue"] = "anchor-cases-queue" });

        Assert.StartsWith("ellai-curated-curated-queue-tour-001-1.0", definition.Id, StringComparison.Ordinal);
        Assert.Single(definition.Steps);
        Assert.Equal("see-queue", definition.Steps[0].Id);
        Assert.False(definition.Steps[0].Target.IsNone);
    }
}
