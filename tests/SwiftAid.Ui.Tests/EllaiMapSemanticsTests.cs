using System.Reflection;
using Microsoft.AspNetCore.Components;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Surfaces.Operations;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiMapSemanticsTests
{
    [Theory]
    [InlineData(false, false, UiObservationState.Unknown)]
    [InlineData(true, false, UiObservationState.Ready)]
    [InlineData(false, true, UiObservationState.Disabled)]
    [InlineData(true, true, UiObservationState.Disabled)]
    public void MapRequiresSuccessfulInitialization(bool ready, bool failed, UiObservationState expected)
    {
        var component = new OperationsStreetMap();
        typeof(OperationsStreetMap).GetField("_mapReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, ready);
        typeof(OperationsStreetMap).GetField("_unavailable", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, failed);
        Assert.Equal(6, component.GetState().Controls.Count);
        Assert.All(component.GetState().Controls, control => Assert.Equal(expected, control.State));
        Assert.All(component.Describe().Controls, control => Assert.Equal(new[] { "HighlightControl" }, control.AllowedIntentBindings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDoesNotAdvertiseAbsentHeadingOrRefresh(bool compact)
    {
        var component = new OperationsMapSurface();
        typeof(OperationsMapSurface).GetProperty(nameof(component.CompactPreview))!.SetValue(component, compact);
        typeof(OperationsMapSurface).GetProperty(nameof(component.OnRetry))!.SetValue(component, EventCallback.Factory.Create(this, () => { }));
        var controls = component.GetState().Controls;
        Assert.Equal(compact ? UiObservationState.Unmounted : UiObservationState.Ready, controls.Single(c => c.ControlId == "surveillance-map.overview").State);
        Assert.Equal(compact ? UiObservationState.Unmounted : UiObservationState.Ready, controls.Single(c => c.ControlId == "surveillance-map.refresh").State);
        Assert.Equal(UiObservationState.Unmounted, controls.Single(c => c.ControlId == "surveillance-map.filters").State);
    }
}
