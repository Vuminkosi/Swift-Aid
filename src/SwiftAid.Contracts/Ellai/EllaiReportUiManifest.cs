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
            InputsFor(controlId).Count > 0 ? ["HighlightControl", "UpdatePublicReportDraft"] : ["HighlightControl"],
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
                    proofPolicyId is null ? [] : [proofPolicyId]) { Inputs = InputsFor(controlId) }
        };

    private static IReadOnlyList<EllaiUiInputDescriptor> InputsFor(string controlId) => controlId switch
    {
        "report.subject" =>
        [
            new("report.subject-kind", "Human health or animal health", "selection", true, "Always", "Choose one concern type."),
            new("report.animal-scope", "Individual, herd, flock or other group", "selection", true, "Animal health selected", "Choose the reported animal scope; do not infer it."),
            new("report.gender", "Reported gender", "selection", false, "Human health selected", "Capture only when the reporter explicitly states it; otherwise leave unknown."),
            new("report.death-related", "Is this concern related to a death?", "selection", false, "Human or animal health selected", "Choose yes, no or unknown; do not infer it.")
        ],
        "report.narrative" =>
        [
            new("report.original-statement", "Describe what happened", "multiline-text", false, "Details page", "Follow the current limit shown on the form; a narrative or at least one observation is required."),
            new("report.age-years", "Age in years", "text", false, "Human health selected", "Enter a nonnegative whole number of completed years, or leave unknown."),
            new("report.observation-choices", "Reported observation choices", "multiple-selection", false, "Human or animal choices follow the selected subject", "Select only reported observations. Unselected does not mean absent."),
            new("report.other-observations", "Add other observations", "multiline-text", false, "Details page", "Follow the current count and length limits shown on the form; a narrative or observation is required.")
        ],
        "report.time-location" =>
        [
            new("report.time-precision", "When did this happen?", "selection", true, "Details page", "Unknown, approximate date, date only or exact time; preserve uncertainty."),
            new("report.onset-precision", "When did symptoms or signs start?", "selection", true, "Details page", "Unknown, approximate date, date only or exact time; keep reported onset distinct from occurrence and upload times."),
            new("report.onset-date", "Reported symptom or sign onset date", "date", true, "Onset precision is not Unknown", "Enter only the reported onset date as YYYY-MM-DD."),
            new("report.onset-time", "Reported onset time", "time", true, "Onset precision is Exact", "Enter the reported 24-hour local onset time."),
            new("report.onset-utc-offset", "Onset time difference from UTC", "text", true, "Onset precision is Exact", "Provide the reported UTC offset; do not guess it."),
            new("report.occurrence-date", "Reported date", "date", true, "Time precision is not Unknown", "Enter the reported date as YYYY-MM-DD."),
            new("report.occurrence-time", "Reported time", "time", true, "Time precision is Exact", "Enter a 24-hour time and a valid UTC offset."),
            new("report.utc-offset", "Time difference from UTC", "text", true, "Time precision is Exact", "Provide a valid UTC offset, such as +02:00, matching the reported time."),
            new("report.occurrence-country", "Country where the event happened", "selection", true, "Details page", "Select a country from the current application catalogue."),
            new("report.place-search", "Search for the place", "place-selection", false, "The place registry is available", "Select a reported place; do not infer device location."),
            new("report.region", "Region", "text", false, "Manual place fields are shown", "At most 200 characters; leave unknown names blank."),
            new("report.area", "Area or district", "text", false, "Manual place fields are shown", "At most 200 characters; leave unknown names blank."),
            new("report.locality", "Locality", "text", false, "Manual place fields are shown", "At most 200 characters; leave unknown names blank.")
        ],
        "report.confirmations" =>
        [
            new("report.review-confirmed", "Review the original statement and details", "checkbox", true, "Review page", "The user must explicitly confirm the review."),
            new("report.country-confirmed", "Confirm the event country", "checkbox", true, "Review page with a supported country", "The user must explicitly confirm the country."),
            new("report.processing-confirmed", "Confirm report-handling terms", "checkbox", true, "Current processing policy is available", "The user must explicitly acknowledge the current terms; speech and countdowns are not confirmation.")
        ],
        "report.voice-note" =>
        [new("report.original-voice-note", "Original voice note", "audio-attachment", false, "The existing attachment control is available", "Use the existing attachment workflow and its limits; do not infer attachment completion.")],
        _ => []
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
