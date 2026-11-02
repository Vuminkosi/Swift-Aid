namespace SwiftAid.Contracts.Ellai;

/// <summary>Administrative investigation controls; metadata never grants record or action authority.</summary>
public static class EllaiInvestigationUiManifest
{
    public const string FeatureId = "investigations";
    public const string NavigationId = "nav-investigations";
    public static IReadOnlyList<UiControlDescriptor> Controls { get; } = Array.AsReadOnly(new[]
    {
        Control("overview", "Investigations", "Organize factual evidence for authorized human review; this does not diagnose or confirm an outbreak."),
        Control("list", "Investigation work", "Browse the available investigation work list."),
        Control("title", "Title", "Enter a short investigation title.", "Input"),
        Control("objective", "Administrative objective", "Describe the administrative information to investigate.", "Input"),
        Control("start", "Start investigation", "Start through the existing authorized workflow.", "Button", true),
        Control("detail", "Investigation details", "Review the selected investigation and current status."),
        Control("evidence", "Evidence references", "Review source references; this screen does not import evidence."),
        Control("clarifications", "Factual clarifications", "Review factual questions and use the existing response controls."),
        Control("proposal", "Administrative proposal", "Review the proposed action and its human approval status."),
        Control("execution", "Execution and verification", "Distinguish execution status from verification; approval is not proof of execution."),
        Control("pause", "Pause", "Pause the selected investigation through its authorized workflow.", "Button", true),
        Control("resume", "Resume", "Resume the selected investigation through its authorized workflow.", "Button", true),
        Control("prepare", "Prepare proposal", "Prepare an administrative proposal for review.", "Button", true),
        Control("review-reason", "Review reason", "Record the reason for a human review decision.", "Input"),
        Control("approve", "Approve proposal", "An authorized person must explicitly approve through this control; speech is not authorization.", "Button", true),
        Control("decline", "Decline proposal", "An authorized person may decline the proposal with a reason.", "Button", true),
    });
    public static EllaiUiManifest Definition { get; } = new("operations.investigations", NavigationId, null,
        "Administrative investigation, evidence and human review.", Controls);
    private static UiControlDescriptor Control(string key, string label, string purpose, string kind = "Region", bool consequential = false) =>
        new("investigations." + key, FeatureId, 1, "ellai-uicontract/1.0", "OperationsInvestigationSurface", kind,
            label, purpose, [label], ["investigation", key], [], NavigationId, "ellai-investigation-" + key,
            ["HighlightControl"], consequential ? UiRiskClass.R3 : UiRiskClass.R0,
            [GuidanceMode.SystemTour, GuidanceMode.Navigation], [], [], "explain-" + key,
            consequential ? "proof-action-receipt" : "proof-ui-observed", [EllaiNavigationManifest.Capability], "application-metadata", consequential)
        { Semantics = new(purpose, kind == "Input" ? "text" : null, [], [consequential ? "proof-action-receipt" : "proof-ui-observed"]) };
}
