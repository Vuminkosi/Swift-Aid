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

    public (ValidatedGuidancePlan? Plan, IReadOnlyList<string> Errors) ValidateObserved(
        EllaiGuidancePlanDraft draft, UiRegistryQuery query, IReadOnlySet<string> entitledFeatures,
        IReadOnlySet<string> entitledCapabilities, IReadOnlyList<UiControlObservation> observations,
        string viewInstanceId, string contextStamp, DateTimeOffset now, string planId,
        string? currentStepId = null, IReadOnlySet<string>? completedStepIds = null, IReadOnlySet<string>? validatedControlIds = null, IReadOnlySet<string>? skippedStepIds = null)
    {
        var accepted = Validate(draft, query, entitledFeatures, entitledCapabilities, planId, 1, 0, 0);
        if (accepted.Plan is null) return accepted;
        var errors = new List<string>();
        var snapshot = registry.GetSnapshot(query, entitledFeatures, entitledCapabilities);
        var ordered = EllaiGuidanceSequence.Order(draft.Steps);
        var activeIndex = currentStepId is null ? 0 : ordered.ToList().FindIndex(step => step.StepKey == currentStepId);
        completedStepIds ??= new HashSet<string>();
        skippedStepIds ??= new HashSet<string>();
        var visited = completedStepIds.Concat(skippedStepIds).ToHashSet(StringComparer.Ordinal);
        if (completedStepIds.Overlaps(skippedStepIds) || activeIndex < 0 || visited.Count != activeIndex ||
            ordered.Take(Math.Max(0, activeIndex)).Any(step => !visited.Contains(step.StepKey)))
            return (null, ["Guidance progress must follow the accepted sequence."]);
        var precedingControls = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Count; index++)
        {
            var step = ordered[index];
            var descriptor = snapshot.Descriptors.Single(d => d.ControlId == step.ControlId);
            if (skippedStepIds.Contains(step.StepKey) &&
                (step.Behavior != PlanStepBehavior.Explain || descriptor.RiskClass != UiRiskClass.R0 ||
                 descriptor.HumanConfirmationRequired || descriptor.ProofPolicyId != "proof-ui-observed" ||
                 ordered.Any(other => other.StepKey != step.StepKey && snapshot.Descriptors.Any(control =>
                     control.ControlId == other.ControlId && control.PrerequisiteIds.Contains(step.ControlId)))))
                errors.Add("Skipped progress cannot bypass required interaction or prerequisites.");
            var matches = observations.Where(o => o.ControlId == step.ControlId).ToArray();
            var futureWizardTarget = index > activeIndex && descriptor.FeatureId == EllaiReportUiManifest.FeatureId &&
                !descriptor.HumanConfirmationRequired && descriptor.RiskClass is UiRiskClass.R0 or UiRiskClass.R1 &&
                descriptor.PrerequisiteIds.Count > 0 && descriptor.PrerequisiteIds.All(precedingControls.Contains) &&
                matches.Length == 1 && matches[0].State == UiObservationState.Unmounted;
            var validatedCurrentInput = index == activeIndex && step.Behavior == PlanStepBehavior.GuideHuman &&
                descriptor.ProofPolicyId == "proof-ui-validated" && !descriptor.HumanConfirmationRequired &&
                validatedControlIds?.Contains(step.ControlId) == true && matches.Length == 1 &&
                matches[0].State is UiObservationState.Ready or UiObservationState.Unmounted;
            if (matches.Length != 1 ||
                (index >= activeIndex && matches[0].State != UiObservationState.Ready && !futureWizardTarget && !validatedCurrentInput) ||
                matches[0].DescriptorVersion != descriptor.DescriptorVersion || matches[0].ViewInstanceId != viewInstanceId ||
                matches[0].ContextStamp != contextStamp || now - matches[0].ObservedAtUtc > TimeSpan.FromSeconds(45) || matches[0].ObservedAtUtc > now)
                errors.Add("Guidance requires a fresh, visible and enabled target in the current view.");
            if (!descriptor.SupportedModes.Contains(draft.Mode) || step.ObjectiveId != descriptor.ObjectiveId ||
                step.FactIds.Any(f => !descriptor.ProductFactIds.Contains(f)) || step.EvidenceIds.Any(e => e != step.ControlId) ||
                step.ResourceBindingKey is not null ||
                (step.Behavior is not (PlanStepBehavior.Explain or PlanStepBehavior.GuideHuman) &&
                    !(descriptor.FeatureId == EllaiNavigationManifest.FeatureId &&
                      EllaiNavigationManifest.ResolveStepDestination(step) is not null)))
                errors.Add("Guidance metadata is not grounded in its registered control.");
            if (!skippedStepIds.Contains(step.StepKey)) precedingControls.Add(step.ControlId);
        }
        return errors.Count == 0 ? accepted : (null, errors);
    }

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

            if (descriptor.RiskClass == UiRiskClass.R5 ||
                descriptor.RiskClass == UiRiskClass.R3 && step.Behavior != PlanStepBehavior.GuideHuman)
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
