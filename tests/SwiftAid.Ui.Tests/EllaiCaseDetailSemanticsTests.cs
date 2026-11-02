using System.Reflection;
using System.Text.Json;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Surfaces.Operations.CaseDetail;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiCaseDetailSemanticsTests
{
    [Theory]
    [InlineData(0, "overview")]
    [InlineData(1, "history")]
    [InlineData(2, "follow-ups")]
    [InlineData(3, "assistance")]
    [InlineData(4, "discussion")]
    [InlineData(5, "attachments")]
    [InlineData(6, "observations")]
    public void OnlyActivePanelIsAdvertisedAndNoCaseValuesAreExported(int index, string panel)
    {
        var component = new OperationsCaseDetailSurface();
        typeof(OperationsCaseDetailSurface).GetField("_activePanelIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, index);
        var summary = new CaseDetailSummary(Guid.NewGuid(), "PRIVATE REFERENCE", default, "PRIVATE NARRATIVE", "PRIVATE CLASSIFICATION",
            "Open", "PRIVATE PRIORITY", "PRIVATE COUNTRY", "PRIVATE LOCATION", "PRIVATE OWNER", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        typeof(OperationsCaseDetailSurface).GetProperty(nameof(component.OverviewData))!.SetValue(component,
            new CaseDetailOverviewView(CaseDetailPanelState.Populated, summary));
        var state = component.GetState();
        foreach (var key in new[] { "overview", "history", "follow-ups", "assistance", "discussion", "attachments", "observations" })
            Assert.Equal(key == panel ? UiObservationState.Ready : UiObservationState.Unmounted,
                state.Controls.Single(control => control.ControlId == "case-detail." + key).State);
        Assert.Equal(index == 0 ? UiObservationState.Ready : UiObservationState.Unmounted,
            state.Controls.Single(control => control.ControlId == "case-detail.summary").State);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(state));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(component.Describe()));
        Assert.All(state.Controls, control => Assert.False(control.CompletionAccepted));
    }

    [Theory]
    [InlineData(CaseDetailPanelState.Loading)]
    [InlineData(CaseDetailPanelState.Denied)]
    [InlineData(CaseDetailPanelState.Error)]
    [InlineData(CaseDetailPanelState.Populated)]
    public void HeadingCannotProveThatSummaryLoaded(CaseDetailPanelState status)
    {
        var component = new OperationsCaseDetailSurface();
        typeof(OperationsCaseDetailSurface).GetProperty(nameof(component.OverviewData))!.SetValue(component,
            new CaseDetailOverviewView(status, null));
        var state = component.GetState();
        Assert.Equal(UiObservationState.Ready, state.Controls.Single(control => control.ControlId == "case-detail.heading").State);
        Assert.Equal(UiObservationState.Unmounted, state.Controls.Single(control => control.ControlId == "case-detail.summary").State);
        Assert.Null(EllaiNavigationManifest.ResolveRoute(EllaiCaseDetailUiManifest.NavigationId));
    }
}
