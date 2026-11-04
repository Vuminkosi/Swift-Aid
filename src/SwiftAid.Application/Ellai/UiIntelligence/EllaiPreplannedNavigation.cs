using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Prepares only exact, registered navigation requests; all other requests use normal planning.</summary>
internal static class EllaiPreplannedNavigation
{
    private static readonly IReadOnlyDictionary<string, EllaiNavigationDestination> Requests = BuildRequests();

    public static EllaiGuidancePlanDraft? Create(string objective)
    {
        if (string.IsNullOrWhiteSpace(objective) || !Requests.TryGetValue(objective.Trim(), out var destination)) return null;
        var step = new GuidancePlanStep("step-1", destination.ControlId, "open-" + destination.Id,
            PlanStepBehavior.RequestUiIntent, "NavigateFeature", null, [], [], destination.Purpose, null);
        return new(EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.Ready, objective,
            GuidanceMode.Navigation, [step], [], null);
    }

    private static IReadOnlyDictionary<string, EllaiNavigationDestination> BuildRequests()
    {
        var candidates = EllaiNavigationManifest.Destinations.SelectMany(destination =>
            new[] { "open " + destination.Label, "go to " + destination.Label, "show me " + destination.Label }
                .Select(phrase => (phrase, destination)));
        return candidates.GroupBy(item => item.phrase, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().destination, StringComparer.OrdinalIgnoreCase);
    }
}
