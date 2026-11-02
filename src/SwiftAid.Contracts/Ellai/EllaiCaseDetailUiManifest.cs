namespace SwiftAid.Contracts.Ellai;

/// <summary>Safe case-detail presentation descriptions, never record contents or authorization evidence.</summary>
public static class EllaiCaseDetailUiManifest
{
    public const string FeatureId = "case-detail";
    public const string NavigationId = "case-detail-current";
    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(new[]
    {
        Control("heading", "Case details", "Understand the currently open case-detail page.", "Heading"),
        Control("tabs", "Case sections", "Choose overview, history, follow-ups, assistance, discussion, attachments or observations.", "Tabs"),
        Control("overview", "Overview", "Review the available case overview and its authorized data.", "Panel"),
        Control("history", "History", "Review case activity available to your account.", "Panel"),
        Control("follow-ups", "Follow-ups", "Review follow-up work; changes use the existing authorized workflow.", "Panel"),
        Control("assistance", "Assistance", "Review assistance work; changes use the existing authorized workflow.", "Panel"),
        Control("discussion", "Discussion", "Review internal discussion; posting requires existing permissions.", "Panel"),
        Control("attachments", "Attachments", "Review evidence attachments; uploads and downloads retain their existing authorization.", "Panel"),
        Control("observations", "Observations", "Review recorded observations; recording new information requires the existing form.", "Panel"),
        Control("summary", "Case summary", "Review the loaded case summary; reported information is not a confirmed diagnosis.", "Summary"),
    });
    public static EllaiUiManifest Definition { get; } = new("operations.case-detail", NavigationId, null,
        "Explain the currently open case-detail surface without exporting its data.", Controls);

    private static UiControlDescriptor Control(string key, string label, string purpose, string kind) =>
        new("case-detail." + key, FeatureId, 1, "ellai-uicontract/1.0", "OperationsCaseDetailSurface", kind,
            label, purpose, [label], ["case-detail", key], [], NavigationId, "ellai-case-detail-" + key,
            ["HighlightControl"], UiRiskClass.R0, [GuidanceMode.SystemTour, GuidanceMode.Navigation], [], [],
            "explain-" + key, "proof-ui-observed", [EllaiNavigationManifest.Capability], "application-metadata", false)
        { Semantics = new(purpose, null, [], ["proof-ui-observed"]) };
}
