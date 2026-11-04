namespace SwiftAid.Contracts.Ellai;

/// <summary>Case-list presentation metadata. Never contains search text, identities or case records.</summary>
public static class EllaiCaseListUiManifest
{
    public const string FeatureId = "case-list";
    public const string NavigationId = "nav-cases";
    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(new[]
    {
        Control("overview", "Case management", "Review cases available to your account.", "Heading"),
        Control("search", "Search cases", "Search the authorized case list on desktop.", "Input"),
        Control("mobile-search", "Search cases", "Search the authorized case list on mobile.", "Input"),
        Control("sector", "Human or animal", "Filter reports by health sector.", "Selection"),
        Control("status", "Status", "Filter cases by workflow status.", "Selection"),
        Control("country", "Country", "Filter by an available country.", "Selection"),
        Control("region", "Region", "Filter by an available region.", "Selection"),
        Control("assignee", "Assigned worker", "Filter by an available assigned worker.", "Selection"),
        Control("category", "Reported category", "Filter by the reported category, not a confirmed diagnosis.", "Selection"),
        Control("from", "Updated from", "Set the earliest update date.", "Date"),
        Control("to", "Updated to", "Set the latest update date.", "Date"),
        Control("sort", "Sort cases", "Switch between newest and oldest updates.", "Button"),
        Control("clear", "Clear filters", "Reset the case-list filters.", "Button"),
        Control("refresh", "Refresh cases", "Reload the authorized case list.", "Button"),
        Control("quick", "Quick filters", "Choose a mobile type or status filter.", "Selection"),
        Control("table", "Cases table", "Review authorized case rows on desktop; use existing links to open details.", "Table"),
        Control("cards", "Case cards", "Review authorized case cards on mobile; use existing links to open details.", "List"),
        Control("pages", "Case list pages", "Move between pages of authorized results.", "Pagination"),
    });
    public static EllaiUiManifest Definition { get; } = new("operations.case-list", NavigationId, null,
        "Find and filter cases through the existing authorized list.", Controls);

    private static UiControlDescriptor Control(string key, string label, string purpose, string kind) =>
        new("case-list." + key, FeatureId, 1, "ellai-uicontract/1.0", "OperationsCaseQueue", kind,
            label, purpose, [label], ["cases", "filters", key], [], NavigationId, "ellai-case-list-" + key,
            EllaiCaseFilterProposal.Keys.Contains(key) ? ["HighlightControl", "SetPermittedFilter"] : ["HighlightControl"], UiRiskClass.R0, [GuidanceMode.SystemTour, GuidanceMode.Navigation], [], [],
            "explain-" + key, "proof-ui-observed", [EllaiNavigationManifest.Capability], "application-metadata", false)
        { Semantics = new(purpose, kind is "Input" or "Date" ? kind : null, [], ["proof-ui-observed"]) };
}
