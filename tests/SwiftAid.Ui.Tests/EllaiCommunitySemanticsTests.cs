using System.Reflection;
using System.Text.Json;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Sessions;
using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiCommunitySemanticsTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void BrowsingMetadataReflectsAvailableFiltersWithoutCopyingSessionValues(bool loading, bool error, bool available)
    {
        var component = new PublicSessionsSurface();
        Set(component, nameof(component.Loading), loading);
        Set(component, nameof(component.ErrorMessage), error ? "PRIVATE ERROR" : null);
        Set(component, nameof(component.Sessions), new PublicSessionView[]
        {
            new(Guid.NewGuid(), 1, "PRIVATE TITLE", "PRIVATE SUMMARY", "PRIVATE AUDIENCE", [], "SZ", "PRIVATE REGION", "human",
                "PRIVATE PRESENTER", "en", DateTimeOffset.UtcNow.AddDays(1), "UTC", 60, null, [], default, null, "Published", null, 1)
        });
        typeof(PublicSessionsSurface).GetField("_searchText", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "PRIVATE QUERY");
        var state = component.GetState();
        Assert.Equal(available ? UiObservationState.Ready : UiObservationState.Unmounted,
            state.Controls.Single(control => control.ControlId == "community-sessions.search").State);
        Assert.Equal(UiObservationState.Unmounted, state.Controls.Single(control => control.ControlId == "community-sessions.from").State);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(state));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(component.Describe()));
        Assert.All(component.Describe().Controls, control =>
        {
            Assert.Empty(control.RequiredCapabilities);
            Assert.Equal(new[] { "HighlightControl" }, control.AllowedIntentBindings);
        });
        Assert.Equal(10, component.Describe().Controls.Count);
    }

    private static void Set(PublicSessionsSurface component, string name, object? value) =>
        typeof(PublicSessionsSurface).GetProperty(name)!.SetValue(component, value);
}
