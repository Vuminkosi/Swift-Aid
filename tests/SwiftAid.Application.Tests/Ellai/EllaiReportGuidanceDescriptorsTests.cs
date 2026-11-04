using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T33")]
public sealed class EllaiReportGuidanceDescriptorsTests
{
    [Fact]
    public void ComponentAndGatewayShareOneSemanticManifest()
    {
        Assert.Same(EllaiReportUiManifest.All, EllaiReportGuidanceDescriptors.All);
        Assert.Equal("report.form", EllaiReportUiManifest.Definition.ComponentId);
        Assert.All(EllaiReportUiManifest.Definition.Controls, control =>
        {
            Assert.NotNull(control.Semantics);
            Assert.False(string.IsNullOrWhiteSpace(control.Semantics.Purpose));
            Assert.Contains(control.ProofPolicyId!, control.Semantics.CompletionSignals);
        });
        var submit = EllaiReportUiManifest.All.Single(control => control.ControlId == "report.submit");
        Assert.NotEmpty(submit.Semantics!.ValidationRequirements);
        Assert.True(submit.HumanConfirmationRequired);
    }

    private static EllaiUiRegistry SeededRegistry()
    {
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        return registry;
    }

    [Fact]
    public void GuestResolvesReportEntryThroughGoalTagsWithoutCapabilities()
    {
        var snapshot = SeededRegistry().GetSnapshot(
            new UiRegistryQuery("report", new[] { "create-report", "submit-report" }, 20),
            new HashSet<string> { "report" },
            new HashSet<string>());

        Assert.Contains(snapshot.Descriptors, d => d.ControlId == "report.entry");
        Assert.Equal(EllaiUiRegistry.CurrentCatalogueVersion, snapshot.CatalogueVersion);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public void ReportJourneyInventoryIsCompleteAndOrdered()
    {
        var ids = EllaiReportGuidanceDescriptors.All.Select(d => d.ControlId).ToList();

        Assert.Equal(
            new[]
            {
                "report.entry", "report.subject", "report.narrative", "report.time-location",
                "report.voice-note", "report.review", "report.confirmations",
                "report.submit", "report.receipt", "report.save",
            },
            ids);
    }

    [Fact]
    public void SubmitStaysHumanOwnedSoGeneratedPlansCannotExecuteIt()
    {
        var registry = SeededRegistry();
        var snapshot = registry.GetSnapshot(
            new UiRegistryQuery("report", Array.Empty<string>(), 20),
            new HashSet<string> { "report" },
            new HashSet<string>());
        var mounted = snapshot.Descriptors.Select(d => d.ControlId).ToHashSet(StringComparer.Ordinal);
        var observations = new EllaiUiDiscoveryService().Observe(
            snapshot, mounted, "view-1", "ctx-1", "web", DateTimeOffset.UtcNow,
            mounted.Select(id => new UiControlState(id, UiObservationState.Ready)).ToArray());
        var draft = new EllaiGuidancePlanner().ComposeDraft(
            "Show me how to create and submit a report", GuidanceMode.Navigation, snapshot, observations, 8);

        var submit = EllaiReportGuidanceDescriptors.All.Single(d => d.ControlId == "report.submit");
        Assert.Equal(UiRiskClass.R3, submit.RiskClass);
        Assert.True(submit.HumanConfirmationRequired);
        Assert.DoesNotContain(draft.Steps, s => s.ControlId == "report.submit");

        var (plan, errors) = new EllaiGuidancePlanValidator(registry).Validate(
            draft, new UiRegistryQuery("report", Array.Empty<string>(), 20),
            new HashSet<string> { "report" }, new HashSet<string>(),
            "plan-report-1", 1, 0, 0);

        Assert.NotNull(plan);
        Assert.Empty(errors);
    }

    [Fact]
    public void UnentitledFeatureSeesNoReportControls()
    {
        var snapshot = SeededRegistry().GetSnapshot(
            new UiRegistryQuery("report", new[] { "create-report" }, 20),
            new HashSet<string> { "cases" },
            new HashSet<string>());

        Assert.Empty(snapshot.Descriptors);
    }

    [Fact]
    public void ReceiptCarriesAcceptedIntakeProofPolicy()
    {
        var receipt = EllaiReportGuidanceDescriptors.All.Single(d => d.ControlId == "report.receipt");

        Assert.Equal("intake-receipt-accepted", receipt.ProofPolicyId);
        Assert.Equal("public-receipt-title", receipt.AnchorBindingKey);
    }
}
