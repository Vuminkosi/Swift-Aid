namespace SwiftAid.Contracts.Ellai;

/// <summary>Value-free descriptions of existing organization membership UI; never action authority.</summary>
public static class EllaiOrganizationUiManifest
{
    public const string FeatureId = "organizations";
    public const string NavigationId = "nav-organizations";
    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(new[]
    {
        Control("overview", "Organizations and workspaces", "Understand organization membership and workspace access."),
        Control("memberships", "Your workspaces", "Review your memberships; displayed membership is not permission to perform every action."),
        Control("search", "Search organizations", "Find an organization through the existing directory search.", "Input"),
        Control("directory", "Organization results", "Choose an organization and request access through its existing button; a request does not grant membership.", "Region", true),
        Control("requests", "Your requests", "Review the status of your membership requests."),
        Control("create-name", "Organization id", "Enter the new organization identifier yourself.", "Input"),
        Control("create", "Create organization", "Use the existing create workflow yourself; no organization exists until the server confirms it.", "Button", true),
        Control("invitation-code", "Invitation code", "Enter your invitation code yourself. Never share invitation codes with Ellai.", "Input", true),
        Control("redeem", "Redeem code", "Use the existing redemption workflow yourself; redemption remains subject to server validation.", "Button", true),
        Control("issue-organization", "Organization to invite to", "Select an organization offered by the current invitation form.", "Selection"),
        Control("issue-uses", "Maximum uses", "Choose the invitation use limit, between 1 and 100.", "Input"),
        Control("issue-days", "Valid for days", "Choose the invitation validity, between 1 and 365 days.", "Input"),
        Control("issue", "Create invitation code", "An authorized person must create the invitation through the existing control. The resulting code is private.", "Button", true),
        Control("refresh", "Retry workspaces", "Retry loading workspace information."),
    });
    public static EllaiUiManifest Definition { get; } = new("account.organizations", NavigationId, null,
        "Organization membership, workspace access and human-operated invitations.", Controls);
    private static UiControlDescriptor Control(string key, string label, string purpose, string kind = "Region", bool consequential = false) =>
        new("organizations." + key, FeatureId, 1, "ellai-uicontract/1.0", "OrganizationsSurface", kind,
            label, purpose, [label], ["organization", "workspace", key], [], NavigationId, "ellai-organizations-" + key,
            ["HighlightControl"], consequential ? UiRiskClass.R3 : UiRiskClass.R0,
            [GuidanceMode.SystemTour, GuidanceMode.Navigation], [], [], "explain-" + key,
            consequential ? "proof-action-receipt" : "proof-ui-observed", [EllaiNavigationManifest.Capability], "application-metadata", consequential)
        { Semantics = new(purpose, kind == "Input" ? "text" : null, [], [consequential ? "proof-action-receipt" : "proof-ui-observed"]) };
}
