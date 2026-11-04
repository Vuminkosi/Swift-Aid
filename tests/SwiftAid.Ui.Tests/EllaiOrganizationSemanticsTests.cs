using System.Reflection;
using System.Text.Json;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiOrganizationSemanticsTests
{
    private static OrganizationsSurface Surface(OrganizationsSnapshot snapshot)
    {
        var component = new OrganizationsSurface();
        typeof(OrganizationsSurface).GetProperty(nameof(component.Snapshot))!.SetValue(component, snapshot);
        return component;
    }
    private static UiObservationState State(OrganizationsSurface component, string key) =>
        component.GetState().Controls.Single(control => control.ControlId == "organizations." + key).State;

    [Theory]
    [InlineData(OrganizationsViewState.Loading)]
    [InlineData(OrganizationsViewState.SignedOut)]
    [InlineData(OrganizationsViewState.Unavailable)]
    public void NonReadySurfaceDoesNotAdvertiseMembershipOrInvitationActions(OrganizationsViewState view)
    {
        var component = Surface(new(view, [], [], [], false));
        foreach (var key in new[] { "memberships", "search", "create", "redeem", "issue", "invitation-code" })
            Assert.Equal(UiObservationState.Unmounted, State(component, key));
        Assert.Equal(view == OrganizationsViewState.Unavailable ? UiObservationState.Ready : UiObservationState.Unmounted,
            State(component, "refresh"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InvitationControlsFollowActualEligibilityAndSavingState(bool workspace, bool saving)
    {
        var component = Surface(new(OrganizationsViewState.Ready,
            [new("PRIVATE-ORG", "Active", "Active", workspace ? ["PRIVATE-WS"] : [])], [], [], saving));
        Assert.Equal(!workspace ? UiObservationState.Unmounted : saving ? UiObservationState.Disabled : UiObservationState.Ready,
            State(component, "issue"));
        Assert.Equal(saving ? UiObservationState.Disabled : UiObservationState.Ready, State(component, "create"));
        Assert.Equal(UiObservationState.Unmounted, State(component, "directory"));
        Assert.Equal(UiObservationState.Unmounted, State(component, "requests"));
    }

    [Fact]
    public void SemanticStateNeverContainsCodesIdentitiesSearchOrReasons()
    {
        var component = Surface(new(OrganizationsViewState.Ready,
            [new("PRIVATE-ORG", "Active", "Active", ["PRIVATE-WS"])],
            [new("PRIVATE-REQUEST", "PRIVATE-ORG", "Pending", "PRIVATE-REASON")],
            [new("PRIVATE-DIRECTORY", "Active")], false, "PRIVATE-MESSAGE", true,
            new("PRIVATE-ISSUED-CODE", "PRIVATE-ORG", DateTimeOffset.UtcNow)));
        foreach (var field in new[] { "_code", "_search", "_createOrganizationId", "_issueOrganizationId" })
            typeof(OrganizationsSurface).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, "PRIVATE-INPUT");
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(component.Describe()));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(component.GetState()));
        Assert.True(component.GetState().HasValidationErrors);
        Assert.Equal(UiObservationState.Ready, State(component, "directory"));
        Assert.Equal(UiObservationState.Ready, State(component, "requests"));
        Assert.All(component.GetState().Controls, control => Assert.False(control.CompletionAccepted));
    }

    [Fact]
    public void ConsequentialControlsAreHumanOwnedAndNeverExecutableBindings()
    {
        Assert.Equal(14, EllaiOrganizationUiManifest.Controls.Count);
        Assert.Equal(14, EllaiOrganizationUiManifest.Controls.Select(control => control.AnchorBindingKey).Distinct().Count());
        foreach (var key in new[] { "directory", "create", "invitation-code", "redeem", "issue" })
        {
            var descriptor = EllaiOrganizationUiManifest.Controls.Single(control => control.ControlId == "organizations." + key);
            Assert.Equal(UiRiskClass.R3, descriptor.RiskClass);
            Assert.True(descriptor.HumanConfirmationRequired);
            Assert.Equal("proof-action-receipt", descriptor.ProofPolicyId);
        }
        Assert.All(EllaiOrganizationUiManifest.Controls, control => Assert.Equal(new[] { "HighlightControl" }, control.AllowedIntentBindings));
    }
}
