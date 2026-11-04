using SwiftAid.Contracts.Cases;

namespace SwiftAid.Contracts.Ellai;

/// <summary>Value-free follow-up and escalation metadata; never grants case access.</summary>
public static class EllaiMonitoringUiManifest
{
    public const string FeatureId = "monitoring";
    public const string NavigationId = "nav-monitoring";
    public static string ScopeKey(MonitoringQueueScope scope) => scope == MonitoringQueueScope.Assigned ? "assigned" : "open";
    public static string Id(MonitoringQueueScope scope, string key) => $"monitoring.{ScopeKey(scope)}.{key}";
    public static string Anchor(MonitoringQueueScope scope, string key) => $"ellai-monitoring-{ScopeKey(scope)}-{key}";
    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(
        new[] { MonitoringQueueScope.Assigned, MonitoringQueueScope.Open }.SelectMany(scope => new[]
        {
            Control(scope, "overview", "Monitoring work", "Review authorized follow-ups and escalations, not outbreak signals."),
            Control(scope, "search", "Search summary or status", "Filter the currently available work by summary or status.", "Input"),
            Control(scope, "kind", "Work type", "Filter follow-ups or escalations.", "Input"),
            Control(scope, "status", "Status", "Filter by available work status.", "Input"),
            Control(scope, "assignment", "Assignment", "Filter assigned or unassigned work.", "Input"),
            Control(scope, "due", "Due time", "Filter overdue or scheduled work.", "Input"),
            Control(scope, "table", "Work table", "Review available work and open an authorized case."),
            Control(scope, "cards", "Work cards", "Review available work in the compact layout."),
            Control(scope, "refresh", "Refresh work", "Reload work through the existing authorized service.", "Button"),
        }).ToArray());
    public static EllaiUiManifest Describe(MonitoringQueueScope scope) => new(
        "operations.monitoring." + ScopeKey(scope), NavigationId, null,
        scope == MonitoringQueueScope.Assigned ? "Monitoring work assigned to you." : "Open monitoring work available to you.",
        Controls.Where(control => control.ControlId.StartsWith("monitoring." + ScopeKey(scope) + ".", StringComparison.Ordinal)).ToArray());
    private static UiControlDescriptor Control(MonitoringQueueScope scope, string key, string label, string purpose, string kind = "Region") =>
        new(Id(scope, key), FeatureId, 1, "ellai-uicontract/1.0", "MonitoringQueue", kind,
            label, purpose, [label], ["monitoring", ScopeKey(scope), key], [], NavigationId, Anchor(scope, key),
            ["HighlightControl"], UiRiskClass.R0, [GuidanceMode.SystemTour, GuidanceMode.Navigation], [], [],
            "explain-" + key, "proof-ui-observed", [EllaiNavigationManifest.Capability], "application-metadata", false)
        { Semantics = new(purpose, kind == "Input" ? "text" : null, [], ["proof-ui-observed"]) };
}
