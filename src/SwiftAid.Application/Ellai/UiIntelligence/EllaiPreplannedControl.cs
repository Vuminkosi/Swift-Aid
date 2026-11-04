using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Value-free explanations for one unambiguous, visible low-risk control.</summary>
internal static class EllaiPreplannedControl
{
    public static EllaiGuidancePlanDraft? Create(string objective, UiRegistrySnapshot catalogue,
        IReadOnlyList<UiControlObservation> observations)
    {
        if (string.IsNullOrWhiteSpace(objective) || catalogue.Truncated) return null;
        var normalized = objective.Trim().TrimEnd('?').TrimEnd();
        var prefixes = new[] { "where is ", "show me ", "explain " };
        var prefix = prefixes.FirstOrDefault(value => normalized.StartsWith(value, StringComparison.OrdinalIgnoreCase));
        if (prefix is null) return null;
        var label = normalized[prefix.Length..].Trim();
        if (label.Length == 0) return null;

        var matches = catalogue.Descriptors.Where(descriptor =>
            descriptor.RiskClass == UiRiskClass.R0 && !descriptor.HumanConfirmationRequired &&
            descriptor.ProofPolicyId == "proof-ui-observed" && descriptor.SupportedModes.Contains(GuidanceMode.Navigation) &&
            !string.IsNullOrWhiteSpace(descriptor.ObjectiveId) && !string.IsNullOrWhiteSpace(descriptor.Semantics?.Purpose) &&
            descriptor.Aliases.Append(descriptor.LabelResourceKey).Any(alias =>
                string.Equals(alias, label, StringComparison.OrdinalIgnoreCase)) &&
            observations.Count(observation => observation.ControlId == descriptor.ControlId && observation.State == UiObservationState.Ready) == 1)
            .ToArray();
        if (matches.Length != 1) return null;
        var target = matches[0];
        var step = new GuidancePlanStep("step-1", target.ControlId, target.ObjectiveId!,
            PlanStepBehavior.Explain, null, null, [], [], target.Semantics!.Purpose, null);
        return new(EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.Ready, objective,
            GuidanceMode.Navigation, [step], [], null);
    }
}
