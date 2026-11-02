namespace SwiftAid.Contracts.Ellai;

/// <summary>Map presentation capabilities without coordinates, counts or case data.</summary>
public static class EllaiSurveillanceUiManifest
{
    public const string FeatureId = "surveillance-map";
    public const string NavigationId = "nav-surveillance";
    public static IReadOnlyList<UiControlDescriptor> SurfaceControls { get; } = Array.AsReadOnly(new[]
    {
        Control("overview", "Surveillance map", "Explore authorized reported patterns; signals require human verification.", "Heading"),
        Control("filters", "Map filters", "Choose the available country and time window.", "Filters"),
        Control("refresh", "Refresh map", "Reload available authorized map totals.", "Button"),
    });
    public static IReadOnlyList<UiControlDescriptor> MapControls { get; } = Array.AsReadOnly(new[]
    {
        Control("canvas", "Interactive map", "Scroll or pinch to zoom; drag or use arrow keys to pan. Report groups are not individual case locations or confirmed outbreaks.", "Map"),
        Control("reset", "Reset view", "Restore the selected country view.", "Button"),
        Control("human", "Human reports", "Toggle human report groups.", "Toggle"),
        Control("animal", "Animal reports", "Toggle animal report groups.", "Toggle"),
        Control("both", "Both sectors", "Toggle human and animal report layers together.", "Toggle"),
        Control("facilities", "Medical centers and clinics", "Toggle mapped facilities; services and opening hours are not verified.", "Toggle"),
    });
    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(SurfaceControls.Concat(MapControls).ToArray());
    public static EllaiUiManifest Surface { get; } = new("operations.surveillance", NavigationId, null, "Explore authorized report aggregates.", SurfaceControls);
    public static EllaiUiManifest Map { get; } = new("operations.street-map", NavigationId, "operations.surveillance", "Operate the shared map presentation.", MapControls);
    private static UiControlDescriptor Control(string key, string label, string purpose, string kind) =>
        new("surveillance-map." + key, FeatureId, 1, "ellai-uicontract/1.0", "OperationsMap", kind,
            label, purpose, [label], ["surveillance", "map", key], [], NavigationId, "ellai-map-" + key,
            ["HighlightControl"], UiRiskClass.R0, [GuidanceMode.SystemTour, GuidanceMode.Navigation], [], [],
            "explain-" + key, "proof-ui-observed", [EllaiNavigationManifest.Capability], "application-metadata", false)
        { Semantics = new(purpose, null, [], ["proof-ui-observed"]) };
}
