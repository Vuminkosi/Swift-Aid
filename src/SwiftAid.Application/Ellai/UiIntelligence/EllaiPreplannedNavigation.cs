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
                .Select(phrase => (phrase, destination))).ToList();
        var aliases = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["cases"] = ["open my cases", "show my cases", "show me my cases"],
            ["assignments"] = ["open my assignments", "show my assignments", "show me my assignments"],
            ["follow-ups"] = ["open follow ups", "show my follow ups", "show me my follow ups"],
            ["escalations"] = ["open my escalations", "show my escalations", "show me my escalations"],
            ["organizations"] = ["open organizations", "show my organizations", "show me my organizations"],
            ["sessions"] = ["open community sessions", "show community sessions", "show me community sessions"],
            ["account"] = ["open my account", "open account settings", "show my settings"]
        };
        foreach (var destination in EllaiNavigationManifest.Destinations)
            if (aliases.TryGetValue(destination.Id, out var phrases))
                candidates.AddRange(phrases.Select(phrase => (phrase, destination)));
        return candidates.GroupBy(item => item.phrase, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.destination.Id).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().destination, StringComparer.OrdinalIgnoreCase);
    }
}
