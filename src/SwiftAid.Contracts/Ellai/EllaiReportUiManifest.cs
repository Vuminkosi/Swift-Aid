namespace SwiftAid.Contracts.Ellai;

/// <summary>Production UI descriptors for the public report journey behind
/// route <c>/report</c> (<c>PublicReportSurface</c> rendering
/// <c>GuidedReportStepper</c>, plus <c>PublicReportReceipt</c>).
/// Anchor binding keys reference stable Bonboarding anchors rendered by these
/// components; the trusted presentation adapter resolves them, the model
/// never receives selectors. Submit stays human-owned (R3) so generated
/// plans can explain it but only the reporter's ordinary confirmation
/// performs it. Guest intake needs no capability beyond the <c>report</c>
/// feature itself: no account is required.</summary>
public static class EllaiReportUiManifest
{
    public const string FeatureId = "report";

    public const string NavigationId = "nav-report";

    private static UiControlDescriptor Describe(
        string controlId,
        string anchorBindingKey,
        string elementKind,
        UiRiskClass riskClass,
        string[] goalTags,
        string[] productFactIds,
        string[] prerequisites,
        string[] successors,
        string? objectiveId,
        string? proofPolicyId,
        bool humanConfirmationRequired) => new(
            controlId,
            FeatureId,
            1,
            "ellai-uicontract/1.0",
            "PublicReporting",
            elementKind,
            $"report:{controlId}:label",
            $"report:{controlId}:description",
            Array.Empty<string>(),
            goalTags,
            productFactIds,
            NavigationId,
            anchorBindingKey,
            ["HighlightControl"],
            riskClass,
            [GuidanceMode.Navigation, GuidanceMode.SystemTour, GuidanceMode.Presentation],
            prerequisites,
            successors,
            objectiveId,
            controlId is "report.subject" or "report.narrative" or "report.time-location" ? "proof-ui-validated" : proofPolicyId,
            Array.Empty<string>(),
            "public-report",
            humanConfirmationRequired)
        {
            Semantics = new EllaiUiSemantics(
                Purpose(controlId), elementKind == "Input" ? "report-field" : null,
                controlId == "report.submit" ? ["Report validation and processing confirmation must succeed."] : [],
                controlId is "report.subject" or "report.narrative" or "report.time-location" ? ["proof-ui-validated"] :
                    proofPolicyId is null ? [] : [proofPolicyId])
        };

    private static string Purpose(string controlId) => controlId switch
    {
        "report.entry" => "Open the public report form.",
        "report.subject" => "Choose whether the observation concerns a person or an animal, then use Continue to details to confirm this form step.",
        "report.narrative" => "Record original observations without diagnosing or inventing missing facts.",
        "report.time-location" => "Select the country and location where the event occurred.",
        "report.voice-note" => "Optionally attach an original voice note as evidence.",
        "report.review" => "Review entered observations before submission.",
        "report.confirmations" => "Confirm the report handling terms through the real checkbox.",
        "report.submit" => "The user submits the validated report through the existing application workflow.",
        "report.receipt" => "Read the server-issued intake receipt after successful persistence.",
        "report.save" => "Save the current report on this device. Completion requires confirmed local storage of these values; this does not submit the report.",
        _ => throw new ArgumentOutOfRangeException(nameof(controlId))
    };

    public static IReadOnlyList<UiControlDescriptor> All { get; } =
    [
        Describe(
            "report.entry",
            "guided-report-title",
            "Heading",
            UiRiskClass.R0,
            ["create-report", "submit-report", "report-help"],
            ["public-intake-no-account"],
            [],
            ["report.subject"],
            "open-report-form",
            "proof-ui-observed",
            false),
        Describe(
            "report.subject",
            "report-type-title",
            "Input",
            UiRiskClass.R1,
            ["create-report", "submit-report", "human-report", "animal-report"],
            ["public-intake-no-account"],
            ["report.entry"],
            ["report.narrative"],
            "choose-report-subject",
            "proof-ui-observed",
            false),
        Describe(
            "report.narrative",
            "report-details-title",
            "Input",
            UiRiskClass.R1,
            ["create-report", "submit-report", "human-report", "animal-report"],
            ["public-intake-no-account"],
            ["report.subject"],
            ["report.time-location"],
            "capture-report-narrative",
            "proof-ui-observed",
            false),
        Describe(
            "report.time-location",
            "guided-report-country",
            "Input",
            UiRiskClass.R1,
            ["create-report", "submit-report"],
            ["public-intake-no-account"],
            ["report.narrative"],
            ["report.voice-note", "report.review"],
            "capture-report-time-location",
            "proof-ui-observed",
            false),
        Describe(
            "report.voice-note",
            "public-report-voice-title",
            "Input",
            UiRiskClass.R1,
            ["create-report", "submit-report"],
            ["public-intake-no-account"],
            ["report.time-location"],
            ["report.review"],
            "capture-original-voice-note",
            "proof-ui-observed",
            false),
        Describe(
            "report.review",
            "report-review-title",
            "Panel",
            UiRiskClass.R1,
            ["submit-report"],
            ["intake-receipt-is-not-casework"],
            ["report.time-location"],
            ["report.confirmations"],
            "review-report-facts",
            "proof-ui-observed",
            false),
        Describe(
            "report.confirmations",
            "report-processing-confirmation",
            "Input",
            UiRiskClass.R2,
            ["submit-report"],
            ["intake-receipt-is-not-casework"],
            ["report.review"],
            ["report.submit"],
            "confirm-report-processing",
            "proof-human-acknowledged",
            true),
        Describe(
            "report.submit",
            "report-submit",
            "Button",
            UiRiskClass.R3,
            ["submit-report"],
            ["intake-receipt-is-not-casework"],
            ["report.confirmations"],
            ["report.receipt"],
            "submit-report",
            "intake-receipt-accepted",
            true),
        Describe(
            "report.receipt",
            "public-receipt-title",
            "Panel",
            UiRiskClass.R1,
            ["submit-report"],
            ["intake-receipt-is-not-casework"],
            ["report.submit"],
            [],
            "verify-report-receipt",
            "intake-receipt-accepted",
            false),
        Describe(
            "report.save",
            "report-save-draft",
            "Button",
            UiRiskClass.R1,
            ["save-report-draft", "save-draft", "offline-report"],
            ["public-intake-no-account"],
            ["report.subject"],
            [],
            "save-report-draft",
            "proof-ui-validated",
            false) with { DescriptorVersion = 2, AllowDeferredEnablement = true },
    ];

    /// <summary>Shared by rendered components and the trusted tour compiler's anchor map.</summary>
    public static string AnchorFor(string controlId) =>
        All.Single(control => control.ControlId == controlId).AnchorBindingKey;

    public static EllaiUiManifest Definition { get; } = new(
        "report.form", NavigationId, null,
        "Capture observations, review the report and submit through public intake. A receipt does not grant case access.", All);
}
