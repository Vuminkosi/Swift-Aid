using System.Text.Json;
using Microsoft.AspNetCore.Components;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Surfaces.Operations;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiInvestigationSemanticsTests
{
    [Fact]
    public void EmptyInvestigationDoesNotAdvertiseRecordActions()
    {
        var component = new OperationsInvestigationSurface();
        var controls = component.GetState().Controls;
        foreach (var key in new[] { "pause", "resume", "prepare", "approve", "decline", "evidence", "execution" })
            Assert.Equal(UiObservationState.Unmounted, controls.Single(control => control.ControlId == "investigations." + key).State);
        Assert.Equal(UiObservationState.Disabled, controls.Single(control => control.ControlId == "investigations.start").State);
    }

    [Theory]
    [InlineData(false, UiObservationState.Ready)]
    [InlineData(true, UiObservationState.Disabled)]
    public void HumanReviewStateExcludesPrivateDataAndNeverGrantsAnActionBinding(bool busy, UiObservationState expected)
    {
        var component = new OperationsInvestigationSurface();
        Set(component, nameof(component.Snapshot), new OperationsInvestigationSnapshot("PRIVATE-ID", OperationsInvestigationStatus.AwaitingApproval,
            "PRIVATE-TITLE", "PRIVATE-OBJECTIVE", [], [], new("PRIVATE-PROPOSAL", "PRIVATE-SUMMARY", true, false, "Pending"), 1));
        Set(component, nameof(component.DetailState), OperationsSurfaceState.Populated);
        Set(component, nameof(component.Busy), busy);
        Set(component, nameof(component.OnReviewProposal), EventCallback.Factory.Create<OperationsInvestigationProposalReview>(this, _ => { }));
        typeof(OperationsInvestigationSurface).GetField("_reviewReason", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(component, "PRIVATE-REASON");
        var state = component.GetState();
        Assert.Equal(expected, state.Controls.Single(control => control.ControlId == "investigations.approve").State);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(state));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(component.Describe()));
        var approval = component.Describe().Controls.Single(control => control.ControlId == "investigations.approve");
        Assert.True(approval.HumanConfirmationRequired);
        Assert.Equal(UiRiskClass.R3, approval.RiskClass);
        Assert.Equal("proof-action-receipt", approval.ProofPolicyId);
        Assert.All(component.Describe().Controls, control => Assert.Equal(new[] { "HighlightControl" }, control.AllowedIntentBindings));
    }

    private static void Set(OperationsInvestigationSurface component, string name, object value) =>
        typeof(OperationsInvestigationSurface).GetProperty(name)!.SetValue(component, value);
}
