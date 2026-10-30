using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Application-side plan validator: derives risk, required
/// preparation, minimum proof and confirmation from trusted registry
/// metadata. The model cannot weaken them. Produces the immutable
/// ValidatedGuidancePlan identity on success. (ELL-T36 groundwork.)</summary>
public sealed class EllaiGuidancePlanValidator(EllaiUiRegistry registry)
{
    public const int MaxAcceptedStepsPerAttempt = 40;

    public const int MaxReplanDispatches = 3;

    public (ValidatedGuidancePlan? Plan, IReadOnlyList<string> Errors) Validate(
        EllaiGuidancePlanDraft draft,
        UiRegistryQuery query,
        IReadOnlySet<string> entitledFeatures,
        IReadOnlySet<string> entitledCapabilities,
        string planId,
        int revision,
        int acceptedStepCountSoFar,
        int replanDispatchesSoFar)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var errors = new List<string>(EllaiDecisionValidator.ValidateDraft(draft));
        if (errors.Count > 0 || draft.Status != GuidancePlanStatus.Ready)
        {
            return (null, errors);
        }

        if (replanDispatchesSoFar > MaxReplanDispatches)
        {
            errors.Add("Replan dispatch allowance exhausted.");
            return (null, errors);
        }

        if (acceptedStepCountSoFar + draft.Steps.Count > MaxAcceptedStepsPerAttempt)
        {
            errors.Add("Accepted-step budget for this guidance attempt exhausted.");
            return (null, errors);
        }

        var snapshot = registry.GetSnapshot(query, entitledFeatures, entitledCapabilities);
        var byId = snapshot.Descriptors.ToDictionary(d => d.ControlId, StringComparer.Ordinal);

        foreach (var step in draft.Steps)
        {
            if (!byId.TryGetValue(step.ControlId, out var descriptor))
            {
                errors.Add($"Step {step.StepKey} targets unregistered or unentitled control {step.ControlId}.");
                continue;
            }

            if (descriptor.RiskClass is UiRiskClass.R3 or UiRiskClass.R5)
            {
                errors.Add($"Step {step.StepKey} targets human-owned or excluded control {step.ControlId}.");
            }

            if (step.Behavior == PlanStepBehavior.RequestUiIntent
                && step.UiIntentBindingId is not null
                && !descriptor.AllowedIntentBindings.Contains(step.UiIntentBindingId))
            {
                errors.Add($"Step {step.StepKey} binding {step.UiIntentBindingId} is not registered for {step.ControlId}.");
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var plan = new ValidatedGuidancePlan(
            planId, revision, null, "Initial acceptance",
            draft.Steps.Select(s => s.StepKey).ToList(),
            Array.Empty<string>(), Array.Empty<string>(), draft.Steps.ToList(),
            Array.Empty<string>(),
            UiRegistrySnapshotCatalogue(snapshot), snapshot.UiContractVersion);
        return (plan, errors);
    }

    private static string UiRegistrySnapshotCatalogue(UiRegistrySnapshot snapshot) => snapshot.CatalogueVersion;
}
