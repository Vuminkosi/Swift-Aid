using Bit.Bonboarding;
using Microsoft.JSInterop;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed partial class EllaiSurfaceLogicTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InputTourPreservesVisibleOptionalFieldsButFollowsExplicitPageContinuation(bool mounted, bool advances)
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), "");
        var tour = System.Reflection.DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "subject" };
        var step = new GuidancePlanStep("subject", "report.subject", "subject", PlanStepBehavior.GuideHuman,
            null, null, [], [], "Choose the subject.", null);
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["subject"], [], [], [step], [], "catalogue", "contract"));
        SetField(host, "_guidanceFeatureId", EllaiReportUiManifest.FeatureId);
        SetField(host, "_latestGuidanceObservation", new UiObservationReport("view", "context", "web",
            mounted ? ["report.subject"] : [], CompletedControlIds: ["report.subject"]));
        await InvokeAsync(host, "AdvanceValidatedInputAsync", CancellationToken.None);
        Assert.Equal(advances ? new[] { "NextAsync" } : Array.Empty<string>(), proxy.Calls);
    }

    [Fact]
    public async Task ReportInputTourVisitsAgeAndEachSymptomGroupAndSupportsBack()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(1), "");
        var tour = System.Reflection.DispatchProxy.Create<IBonboardingTourSession, NoOpBonboardingServiceProxy>();
        var proxy = (NoOpBonboardingServiceProxy)(object)tour;
        proxy.TourStatus = BonboardingTourStatus.Running;
        proxy.CurrentStep = new BonboardingStepDefinition { Id = "details" };
        var step = new GuidancePlanStep("details", "report.narrative", "record-observations", PlanStepBehavior.GuideHuman,
            null, null, [], [], "Complete the details.", null);
        SetField(host, "_generatedTour", tour);
        SetField(host, "_generatedPlan", new ValidatedGuidancePlan("plan", 1, null, "Initial", ["details"], [], [], [step], [], "catalogue", "contract"));
        SetField(host, "_guidanceFeatureId", EllaiReportUiManifest.FeatureId);
        var anchors = new Dictionary<string, string> { ["report.narrative"] = "section" };
        SetField(host, "_guidanceAnchors", anchors);
        SetField(host, "_uiObservationModule", new InputTourJsModule());
        // Next may arrive before the periodic observer initializes a new section.
        // It must start at its first field and the observer must not rewind it.
        SetField(host, "_reportInputIndex", 5);
        SetField(host, "_reportInputStep", "previous-plan:subject");
        await InvokeAsync(host, "MoveReportInputAsync", 1, CancellationToken.None);
        foreach (var anchor in new[] { "ellai-report-original-statement", "ellai-report-age-years", "ellai-report-core-symptoms",
            "ellai-report-signs-of-dehydration", "ellai-report-additional-symptoms-and-signs", "ellai-report-other-observations",
            "ellai-report-onset-precision", "ellai-report-onset-date", "ellai-report-onset-time", "ellai-report-onset-utc-offset",
            "ellai-report-time-precision", "ellai-report-occurrence-date", "ellai-report-occurrence-time", "ellai-report-utc-offset",
            "ellai-report-place", "ellai-report-place-search", "ellai-report-place-browse", "ellai-report-place-country",
            "ellai-report-place-region", "ellai-report-place-locality", "ellai-report-region", "ellai-report-area", "ellai-report-locality",
            "ellai-report-open-review" })
        {
            Assert.Equal(anchor, anchors["report.narrative"]);
            await InvokeAsync(host, "InitializeReportInputAsync", CancellationToken.None);
            Assert.Equal(anchor, anchors["report.narrative"]);
            await InvokeAsync(host, "MoveReportInputAsync", 1, CancellationToken.None);
        }
        await InvokeAsync(host, "MoveReportInputAsync", -1, CancellationToken.None);
        Assert.Equal("ellai-report-open-review", anchors["report.narrative"]);
        Assert.DoesNotContain("NextAsync", proxy.Calls);
    }

    private sealed class InputTourJsModule : IJSObjectReference
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)(identifier == "prepareReportInput" && (string?)args?[0] != "ellai-report-eating-and-activity"));
    }
}
