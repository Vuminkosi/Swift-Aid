using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Production UI descriptors for the public report journey behind
/// route <c>/report</c> (<c>PublicReportSurface</c> rendering
/// <c>GuidedReportStepper</c>, plus <c>PublicReportReceipt</c>).
/// Anchor binding keys reference stable element IDs rendered by those
/// stepper; the trusted presentation adapter resolves them, the model
/// never receives selectors. Submit stays human-owned (R3) so generated
/// plans can explain it but only the reporter's ordinary confirmation
/// performs it. Guest intake needs no capability beyond the <c>report</c>
/// feature itself: no account is required.</summary>
public static class EllaiReportGuidanceDescriptors
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
            EllaiUiRegistry.CurrentUiContractVersion,
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
            proofPolicyId,
            Array.Empty<string>(),
            "public-report",
            humanConfirmationRequired);

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
            "report-review-title",
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
    ];

    public static void RegisterAll(EllaiUiRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var descriptor in All)
        {
            registry.Register(descriptor);
        }
    }
}
