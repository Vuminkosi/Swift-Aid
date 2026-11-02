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
    public void GeneratedGuidanceKeepsFormAndComposerInteractive()
    {
        var definition = new EllaiBonboardingCompiler().Compile(Plan(), new Dictionary<string, string>());
        Assert.All(definition.Steps, step => Assert.Equal(BonboardingInteractionMode.Free, step.Interaction));
        Assert.All(definition.Steps, step => Assert.NotNull(step.CanAdvance));
    }

    [Fact]
    public void CompilerFollowsValidatedLinksForNextAndPreviousPresentation()
    {
        var first = Plan().Steps[0] with { NextStepKey = "s3" };
        var plan = Plan() with { Steps = [first, first with { StepKey = "s2", NextStepKey = null },
            first with { StepKey = "s3", NextStepKey = "s2" }] };
        var definition = new EllaiBonboardingCompiler().Compile(plan, new Dictionary<string, string>());
        Assert.Equal(new[] { "s1", "s3", "s2" }, definition.Steps.Select(step => step.Id));
    }

    [Fact]
    public void NavigationUsesRegisteredBonboardingRouteRatherThanModelUrls()
    {
        var step = new GuidancePlanStep("navigate", "navigation.cases", "open-cases", PlanStepBehavior.RequestUiIntent,
            "NavigateFeature", null, [], [], "Open cases.", null);
        var plan = Plan() with { Steps = [step] };
        var compiler = new EllaiBonboardingCompiler();
        var definition = compiler.Compile(plan, new Dictionary<string, string> { ["navigation.cases"] = "ellai-navigation-cases" });
        Assert.Equal("/my-cases", Assert.Single(definition.Steps).Route);
        Assert.True(definition.Steps[0].Target.IsDynamic);
        Assert.NotNull(definition.Steps[0].CanAdvance);
        foreach (var invalid in new[]
        {
            step with { ControlId = "https://example.com" },
            step with { ResourceBindingKey = "another-owner-case" },
            step with { UiIntentBindingId = "ExecuteAction" },
            step with { ObjectiveId = "open-admin" },
        })
            Assert.Throws<InvalidOperationException>(() => compiler.Compile(plan with { Steps = [invalid] }, new Dictionary<string, string>()));
        Assert.All(compiler.Compile(Plan(), new Dictionary<string, string>()).Steps, item => Assert.Null(item.Route));
    }

    [Fact]
    public void AcceptedPlanCompilesToRealTourDefinition()
    {
        var compiler = new EllaiBonboardingCompiler();
        var definition = compiler.Compile(
            Plan(), new Dictionary<string, string> { ["cases.queue"] = "anchor-cases-queue" });

        Assert.StartsWith("ellai-generated-plan-1-r1", definition.Id, StringComparison.Ordinal);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal("Ellai guidance", definition.Steps[0].Title);
        Assert.Equal("See the queue.", definition.Steps[0].Description);
        Assert.False(definition.Steps[0].Target.IsNone);
        Assert.True(definition.Steps[0].Target.IsDynamic);
    }

    [Fact]
    public async Task TargetsResolveAtActivationAndMissingBindingsFailClosed()
    {
        var anchors = new Dictionary<string, string>();
        var definition = new EllaiBonboardingCompiler().Compile(Plan(), anchors);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await definition.Steps[0].Target.ResolveAsync(null!));
        anchors["cases.queue"] = "late-queue";
        var target = await definition.Steps[0].Target.ResolveAsync(null!);
        Assert.False(target.IsNone);
        Assert.False(target.IsDynamic);
        Assert.Equal(BonboardingMissingTargetBehavior.Fail, definition.Steps[0].WaitTimeoutBehavior);
    }

    [Fact]
    public async Task DisplayingAStepDoesNotProveCompletion()
    {
        var definition = new EllaiBonboardingCompiler().Compile(Plan(), new Dictionary<string, string>());
        Assert.NotNull(definition.Steps[0].CanAdvance);
        Assert.False(await definition.Steps[0].CanAdvance!(null!));
    }

    [Fact]
    public async Task CompletionIsRecheckedForTheCurrentStepAndCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var completed = false;
        var definition = new EllaiBonboardingCompiler().Compile(Plan(), new Dictionary<string, string>(),
            (stepId, token) =>
            {
                Assert.Equal("s1", stepId);
                Assert.Equal(cancellation.Token, token);
                return ValueTask.FromResult(completed);
            });
        var step = definition.Steps[0];
        var context = new BonboardingStepContext(null!, step, 0, null!, cancellation.Token);
        Assert.False(await step.CanAdvance!(context));
        completed = true;
        Assert.True(await step.CanAdvance!(context));
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
