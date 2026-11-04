using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Value-free, one-step guidance for exact public report questions.</summary>
internal static class EllaiPreplannedReport
{
    private static readonly IReadOnlyDictionary<string, string> Requests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["choose report subject"] = "report.subject",
        ["enter report details"] = "report.narrative",
        ["enter report location"] = "report.time-location",
        ["review report"] = "report.review",
        ["confirm report handling"] = "report.confirmations"
    };

    public static EllaiGuidancePlanDraft? Create(string objective, UiRegistrySnapshot catalogue)
    {
        if (string.IsNullOrWhiteSpace(objective) || !Requests.TryGetValue(objective.Trim(), out var controlId)) return null;
        var descriptor = catalogue.Descriptors.SingleOrDefault(item => item.ControlId == controlId && item.FeatureId == EllaiReportUiManifest.FeatureId);
        if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.ObjectiveId) || string.IsNullOrWhiteSpace(descriptor.Semantics?.Purpose)) return null;
        var step = new GuidancePlanStep("step-1", descriptor.ControlId, descriptor.ObjectiveId,
            PlanStepBehavior.GuideHuman, null, null, [], [], descriptor.Semantics.Purpose, null);
        return new(EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.Ready, objective,
            GuidanceMode.Navigation, [step], [], null);
    }
}
