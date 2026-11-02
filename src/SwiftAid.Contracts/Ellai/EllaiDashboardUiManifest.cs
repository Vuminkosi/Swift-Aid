namespace SwiftAid.Contracts.Ellai;

/// <summary>Shared dashboard component metadata; contains no reported values or record identifiers.</summary>
public static class EllaiDashboardUiManifest
{
    public const string FeatureId = "dashboard";
    public const string NavigationId = "nav-dashboard";
    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(new[]
    {
        Control("overview", "Dashboard", "View the operational overview within your authorized scope.", "Heading"),
        Control("refresh", "Refresh dashboard", "Reload the dashboard using the current filters.", "Button"),
        Control("sector", "Health sector", "Choose human health, animal health or One Health.", "Selection"),
        Control("period", "Period", "Choose the reporting period for the dashboard.", "Selection"),
        Control("country", "Country", "Choose a country within the available authorized scope.", "Selection"),
        Control("summary", "Case and service summary", "Review operational case totals; these are not confirmed disease incidence.", "Summary"),
        Control("recent-cases", "Recent cases", "Review recent authorized reports and use their case links for details.", "Table"),
        Control("map", "Map of recent reports", "Explore grouped reported locations; signals require verification and are not diagnoses.", "Map"),
    });
    public static EllaiUiManifest Definition { get; } = new("operations.dashboard", NavigationId, null,
        "Explore the current authorized operational overview and its filters.", Controls);
    public static string AnchorFor(string controlId) => Controls.Single(control => control.ControlId == controlId).AnchorBindingKey;

    private static UiControlDescriptor Control(string key, string label, string purpose, string kind) =>
        new("dashboard." + key, FeatureId, 1, "ellai-uicontract/1.0", "OperationsDashboard", kind,
            label, purpose, [label], ["dashboard", "overview", key], [], NavigationId, "ellai-dashboard-" + key,
            ["HighlightControl"], UiRiskClass.R0, [GuidanceMode.SystemTour, GuidanceMode.Navigation], [], [],
            "explain-" + key, "proof-ui-observed", [EllaiNavigationManifest.Capability], "application-metadata", false)
        { Semantics = new(purpose, null, [], ["proof-ui-observed"]) };
}
