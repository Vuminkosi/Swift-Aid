namespace SwiftAid.Contracts.Ellai;

/// <summary>Trusted menu destinations. Routes are consumed by presentation adapters, never constructed by a model.</summary>
public sealed record EllaiNavigationDestination(string Id, string Route, string Label, string Purpose, string Group)
{
    public string ControlId => "navigation." + Id;
    public string NavigationId => "nav-" + Id;
    public string Anchor => "ellai-navigation-" + Id;
}

/// <summary>The shared signed-in menu and semantic registry consume the same definitions.
/// Navigation metadata does not grant record access or permission to perform work.</summary>
public static class EllaiNavigationManifest
{
    public const string FeatureId = "navigation";
    public const string Capability = "navigation.authenticated";
    public const string PublicCapability = "navigation.public";
    public static IReadOnlyList<EllaiNavigationDestination> Destinations { get; } = Array.AsReadOnly<EllaiNavigationDestination>(
    [
        new("home", "/", "Home", "Return to the SwiftAid home page.", "Main"),
        new("dashboard", "/dashboard", "Dashboard", "View the authorized operational overview.", "Main"),
        new("cases", "/my-cases", "Cases", "Find and filter cases available to your account.", "Main"),
        new("assignments", "/assignments", "Assignments", "Review your case assignments.", "Main"),
        new("surveillance", "/surveillance", "Surveillance", "Explore authorized surveillance signals and mapped reports.", "Main"),
        new("reports", "/reports", "Reports", "Review authorized reporting summaries.", "Main"),
        new("monitoring", "/monitoring", "Monitoring", "View monitoring information available to your account.", "Main"),
        new("organizations", "/organizations", "Users", "Manage organization membership using existing permissions.", "Main"),
        new("imports", "/workspace/imports", "Imports", "Import case reports into an authorized workspace.", "Workspace"),
        new("assistance", "/assistance", "Assistance", "Review assistance work available to your account.", "Workspace"),
        new("follow-ups", "/follow-ups", "Follow-ups", "Review scheduled follow-up work.", "Workspace"),
        new("escalations", "/escalations", "Escalations", "Review authorized escalations.", "Workspace"),
        new("intake-coordination", "/intake-coordination", "Intake coordination", "Coordinate intake when your account has that permission.", "Workspace"),
        new("investigations", "/investigations", "Investigations", "Review authorized investigations and supporting evidence.", "Workspace"),
        new("sessions", "/sessions", "Community", "Open community sessions.", "Secondary"),
        new("account", "/account", "Settings", "Manage your account and preferences.", "Secondary"),
    ]);

    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(Destinations.Select(item =>
        new UiControlDescriptor(item.ControlId, FeatureId, 1, "ellai-uicontract/1.0", "ApplicationShell", "Navigation",
            item.Label, item.Purpose, [item.Label], ["navigation", "system-tour", item.Id], [], item.NavigationId, item.Anchor,
            ["NavigateFeature", "HighlightControl"], UiRiskClass.R0,
            [GuidanceMode.Navigation, GuidanceMode.SystemTour], [], [], "open-" + item.Id, "proof-ui-observed",
            [item.Id == "home" ? PublicCapability : Capability], "application-metadata", false)
        { Semantics = new(item.Purpose, null, [], ["proof-ui-observed"]) }).ToArray());

    public static EllaiUiManifest Definition { get; } = new("shell.navigation", "nav-dashboard", null,
        "Navigate between available application areas; each destination enforces its own data and action authorization.", Controls);

    public static string? ResolveRoute(string navigationId) =>
        Destinations.SingleOrDefault(item => item.NavigationId == navigationId)?.Route;

    public static string? ResolveNavigationId(string route) =>
        Destinations.SingleOrDefault(item => item.Route == route)?.NavigationId;

    public static EllaiNavigationDestination? ResolveStepDestination(GuidancePlanStep step) =>
        step.Behavior == PlanStepBehavior.RequestUiIntent && step.UiIntentBindingId == "NavigateFeature" &&
        step.ResourceBindingKey is null
            ? Destinations.SingleOrDefault(item => item.ControlId == step.ControlId && step.ObjectiveId == "open-" + item.Id)
            : null;
}
