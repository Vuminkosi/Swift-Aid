using System.Text.Json;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Surfaces.Operations;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiDashboardSemanticsTests
{
    [Theory]
    [InlineData(OperationsSurfaceState.Loading)]
    [InlineData(OperationsSurfaceState.Denied)]
    [InlineData(OperationsSurfaceState.Offline)]
    [InlineData(OperationsSurfaceState.Populated)]
    public void DashboardOnlyAdvertisesMountedContentAndNeverCopiesData(OperationsSurfaceState status)
    {
        var component = new OperationsDashboard();
        typeof(OperationsDashboard).GetProperty(nameof(OperationsDashboard.OverviewState))!.SetValue(component, status);
        Assert.Equal("operations.dashboard", component.ComponentId);
        Assert.All(component.GetState().Controls.Where(control => control.ControlId is "dashboard.summary" or "dashboard.recent-cases" or "dashboard.map"),
            control => Assert.Equal(UiObservationState.Unmounted, control.State));
        typeof(OperationsDashboard).GetProperty(nameof(OperationsDashboard.Overview))!.SetValue(component,
            new DashboardOverviewView("PRIVATE SCOPE", DashboardSectorView.Human, default, default, DateTimeOffset.UtcNow, null, [], [], [], []));
        var state = component.GetState();
        Assert.All(state.Controls.Where(control => control.ControlId is "dashboard.summary" or "dashboard.recent-cases" or "dashboard.map"),
            control => Assert.Equal(status == OperationsSurfaceState.Populated ? UiObservationState.Ready : UiObservationState.Unmounted, control.State));
        Assert.DoesNotContain("PRIVATE SCOPE", JsonSerializer.Serialize(component.Describe()));
        Assert.DoesNotContain("PRIVATE SCOPE", JsonSerializer.Serialize(state));
        Assert.All(state.Controls, control => Assert.False(control.CompletionAccepted));
        Assert.Equal(8, component.Describe().Controls.Count);
        Assert.All(component.Describe().Controls, control =>
        {
            Assert.Equal(EllaiDashboardUiManifest.NavigationId, control.NavigationId);
            Assert.Contains(EllaiNavigationManifest.Capability, control.RequiredCapabilities);
            Assert.Equal(EllaiDashboardUiManifest.AnchorFor(control.ControlId), control.AnchorBindingKey);
        });
    }
}
