using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Value-free guidance for exact public report questions.</summary>
internal static class EllaiPreplannedReport
{
    private static readonly IReadOnlySet<string> WalkthroughRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "guide me through the report",
        "guide me through filing a report",
        "show me how to complete a report",
        "show me how to create and submit a report"
    };

    private static readonly string[] WalkthroughControls =
    [
        "report.entry", "report.subject", "report.narrative", "report.time-location",
        "report.review", "report.confirmations", "report.submit", "report.receipt"
    ];

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
        if (string.IsNullOrWhiteSpace(objective)) return null;
        var normalized = objective.Trim().TrimEnd('?').TrimEnd();
        var controlIds = WalkthroughRequests.Contains(normalized) ? WalkthroughControls :
            Requests.TryGetValue(normalized, out var controlId) ? [controlId] : Array.Empty<string>();
        if (controlIds.Length == 0) return null;
        var descriptors = controlIds.Select(id => catalogue.Descriptors.SingleOrDefault(item =>
            item.ControlId == id && item.FeatureId == EllaiReportUiManifest.FeatureId)).ToArray();
        if (descriptors.Any(descriptor => descriptor is null || string.IsNullOrWhiteSpace(descriptor.ObjectiveId) ||
            string.IsNullOrWhiteSpace(descriptor.Semantics?.Purpose))) return null;
        var steps = descriptors.Select((descriptor, index) => new GuidancePlanStep($"step-{index + 1}",
            descriptor!.ControlId, descriptor.ObjectiveId!, PlanStepBehavior.GuideHuman, null, null, [], [],
            descriptor.Semantics!.Purpose, index + 1 < descriptors.Length ? $"step-{index + 2}" : null)).ToArray();
        return new(EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.Ready, objective,
            GuidanceMode.Navigation, steps, [], null);
    }
}
