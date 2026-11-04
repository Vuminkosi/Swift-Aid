namespace SwiftAid.Contracts.Ellai;

/// <summary>Public session browsing only; no scheduling, attendance, subscription or publication authority.</summary>
public static class EllaiCommunityUiManifest
{
    public const string FeatureId = "community-sessions";
    public const string NavigationId = "nav-sessions";
    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(new[]
    {
        Control("browse", "Community sessions", "Browse published community sessions and their logistics.", "Region"),
        Control("mobile-period", "Upcoming or past", "Choose upcoming or past sessions on mobile.", "Selection"),
        Control("search", "Search sessions", "Search published session logistics.", "Input"),
        Control("more-filters", "More filters", "Reveal additional session filters.", "Disclosure"),
        Control("period", "When", "Choose the session period.", "Selection"),
        Control("category", "Category", "Filter published sessions by category.", "Selection"),
        Control("language", "Language", "Filter published sessions by language.", "Selection"),
        Control("from", "From date", "Set the start of a custom date range.", "Date"),
        Control("to", "To date", "Set the end of a custom date range.", "Date"),
        Control("details", "Session details", "Review the selected published session. Calendar downloads do not record attendance or send invitations.", "Region"),
    });
    public static EllaiUiManifest Definition { get; } = new("public.community-sessions", NavigationId, null,
        "Browse public session logistics; administrative operations retain their existing permissions and confirmations.", Controls);

    private static UiControlDescriptor Control(string key, string label, string purpose, string kind) =>
        new("community-sessions." + key, FeatureId, 1, "ellai-uicontract/1.0", "PublicSessionsSurface", kind,
            label, purpose, [label], ["community", "sessions", key], [], NavigationId, "ellai-community-" + key,
            ["HighlightControl"], UiRiskClass.R0, [GuidanceMode.SystemTour, GuidanceMode.Navigation], [], [],
            "explain-" + key, "proof-ui-observed", [], "public-product-metadata", false)
        { Semantics = new(purpose, kind is "Input" or "Date" ? kind : null, [], ["proof-ui-observed"]) };
}
