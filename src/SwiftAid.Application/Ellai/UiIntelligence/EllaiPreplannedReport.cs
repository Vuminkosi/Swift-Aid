using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Value-free, one-step guidance for exact public report questions.</summary>
internal static class EllaiPreplannedReport
{
    private static readonly IReadOnlyDictionary<string, string> Requests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["choose report subject"] = "report.subject",
        ["choose person or animal"] = "report.subject",
        ["how do i choose the report type"] = "report.subject",
        ["enter report details"] = "report.narrative",
        ["how do i describe what happened"] = "report.narrative",
        ["where do i enter my observations"] = "report.narrative",
        ["enter report location"] = "report.time-location",
        ["where do i enter the location"] = "report.time-location",
        ["how do i enter the date and location"] = "report.time-location",
        ["attach a voice note"] = "report.voice-note",
        ["how do i attach a voice note"] = "report.voice-note",
        ["review report"] = "report.review",
        ["how do i review my report"] = "report.review",
        ["confirm report handling"] = "report.confirmations",
        ["where do i confirm report handling"] = "report.confirmations",
        ["save my report draft"] = "report.save",
        ["how do i save my report draft"] = "report.save"
    };

    public static EllaiGuidancePlanDraft? Create(string objective, UiRegistrySnapshot catalogue)
    {
        if (string.IsNullOrWhiteSpace(objective) || !Requests.TryGetValue(objective.Trim().TrimEnd('?').TrimEnd(), out var controlId)) return null;
        var descriptor = catalogue.Descriptors.SingleOrDefault(item => item.ControlId == controlId && item.FeatureId == EllaiReportUiManifest.FeatureId);
        if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.ObjectiveId) || string.IsNullOrWhiteSpace(descriptor.Semantics?.Purpose)) return null;
        var step = new GuidancePlanStep("step-1", descriptor.ControlId, descriptor.ObjectiveId,
            PlanStepBehavior.GuideHuman, null, null, [], [], descriptor.Semantics.Purpose, null);
        return new(EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.Ready, objective,
            GuidanceMode.Navigation, [step], [], null);
    }
}
