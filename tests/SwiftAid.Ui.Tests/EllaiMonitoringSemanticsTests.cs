using System.Text.Json;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Cases;
using SwiftAid.UI.Components.Surfaces.Operations;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiMonitoringSemanticsTests
{
    [Fact]
    public void ScopesHaveDistinctBoundedAnchorsAndNoActionAuthority()
    {
        var controls = EllaiMonitoringUiManifest.Controls;
        Assert.Equal(18, controls.Count);
        Assert.Equal(18, controls.Select(control => control.ControlId).Distinct().Count());
        Assert.Equal(18, controls.Select(control => control.AnchorBindingKey).Distinct().Count());
        Assert.All(controls, control => Assert.Equal(new[] { "HighlightControl" }, control.AllowedIntentBindings));
        Assert.NotEqual(EllaiMonitoringUiManifest.Describe(MonitoringQueueScope.Assigned).ComponentId,
            EllaiMonitoringUiManifest.Describe(MonitoringQueueScope.Open).ComponentId);
    }

    [Fact]
    public void MobileDoesNotAdvertiseFiltersItDoesNotRender()
    {
        var component = new MonitoringQueue();
        var state = component.GetState();
        Assert.True(state.Loading);
        Assert.All(state.Controls.Where(control => !control.ControlId.EndsWith(".overview")),
            control => Assert.Equal(UiObservationState.Unmounted, control.State));
        Assert.All(state.Controls, control => Assert.False(control.CompletionAccepted));
    }

    [Fact]
    public void EmptyWebQueueHasNoRowsOrRefreshAndNeverIncludesSearchText()
    {
        var component = new OperationsMonitoringQueue();
        typeof(OperationsMonitoringQueue).GetField("_searchText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(component, "PRIVATE-SEARCH");
        var state = component.GetState();
        foreach (var key in new[] { "table", "cards", "refresh" })
            Assert.Equal(UiObservationState.Unmounted, state.Controls.Single(control => control.ControlId.EndsWith("." + key)).State);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(state));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(component.Describe()));
    }
}
