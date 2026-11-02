using System.Text.Json;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Surfaces.Operations;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiCaseListSemanticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilterMetadataTracksCapabilityWithoutExposingSelections(bool enabled)
    {
        var component = new OperationsCaseQueue();
        Set(component, nameof(component.FilterOptions), new OperationsCaseQueue.FilterOptionsView([], [], [], [], [], [],
            enabled, enabled, enabled, enabled, enabled, enabled, enabled, enabled));
        Set(component, nameof(component.Query), new OperationsCaseQueue.FilterRequestView(1, 25, "PRIVATE SEARCH", null,
            null, null, null, null, null, null, null, true));
        var state = component.GetState();
        foreach (var key in new[] { "search", "mobile-search", "sector", "status", "country", "region", "assignee", "category", "from", "to" })
            Assert.Equal(enabled ? UiObservationState.Ready : UiObservationState.Unmounted,
                state.Controls.Single(control => control.ControlId == "case-list." + key).State);
        Assert.DoesNotContain("PRIVATE SEARCH", JsonSerializer.Serialize(state));
        Assert.DoesNotContain("PRIVATE SEARCH", JsonSerializer.Serialize(component.Describe()));
        Assert.All(state.Controls, control => Assert.False(control.CompletionAccepted));
        Assert.Equal(18, component.Describe().Controls.Count);
        Assert.Equal(18, component.Describe().Controls.Select(control => control.AnchorBindingKey).Distinct().Count());
    }

    [Theory]
    [InlineData(OperationsSurfaceState.Loading, false)]
    [InlineData(OperationsSurfaceState.Denied, false)]
    [InlineData(OperationsSurfaceState.Populated, true)]
    public void ResultAnchorsRequireActualRenderedRows(OperationsSurfaceState status, bool rendered)
    {
        var component = new OperationsCaseQueue();
        Set(component, nameof(component.State), status);
        Assert.Equal(UiObservationState.Unmounted, component.GetState().Controls.Single(control => control.ControlId == "case-list.table").State);
        Set(component, nameof(component.Items), new OperationsCaseQueue.QueueViewRow[]
        {
            new(Guid.NewGuid(), "PRIVATE REFERENCE", "Open", "Eswatini", default, "PRIVATE LOCALITY", "PRIVATE WORKER", null, DateTimeOffset.UtcNow)
        });
        foreach (var id in new[] { "case-list.table", "case-list.cards" })
            Assert.Equal(rendered ? UiObservationState.Ready : UiObservationState.Unmounted,
                component.GetState().Controls.Single(control => control.ControlId == id).State);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(component.GetState()));
    }

    private static void Set(OperationsCaseQueue component, string name, object value) =>
        typeof(OperationsCaseQueue).GetProperty(name)!.SetValue(component, value);
}
