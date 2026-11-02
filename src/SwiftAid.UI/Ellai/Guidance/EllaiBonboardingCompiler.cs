using Bit.Bonboarding;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Deterministic compiler: accepted plans and curated manifests become
/// trusted <see cref="BonboardingTourDefinition"/> instances with safe callback
/// mappings. No model calls, navigation, speech, code execution or protected
/// writes. Advancement requires the caller's application-evidence verifier. Targets resolve at activation via <see cref="BonboardingStepTarget.Anchor"/> from
/// the trusted anchor map, or <see cref="BonboardingStepTarget.None"/> for
/// purely informational steps.</summary>
public sealed class EllaiBonboardingCompiler
{
    public BonboardingTourDefinition Compile(
        ValidatedGuidancePlan plan, IReadOnlyDictionary<string, string> anchorByControlId,
        Func<string, CancellationToken, ValueTask<bool>>? verifyCompletion = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(anchorByControlId);

        return new BonboardingTourDefinition
        {
            Id = $"ellai-generated-{plan.PlanId}-r{plan.Revision}",
            Title = "Ellai guidance",
            Steps = EllaiGuidanceSequence.Order(plan.Steps).Select(s => ToStep(
                s.StepKey, s.Explanation, ResolveTarget(s.ControlId, anchorByControlId), verifyCompletion, ResolveRoute(s))).ToList()
        };
    }

    public BonboardingTourDefinition CompileManifest(
        EllaiJourneyManifest manifest, IReadOnlyDictionary<string, string> anchorByControlId,
        Func<string, CancellationToken, ValueTask<bool>>? verifyCompletion = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(anchorByControlId);

        return new BonboardingTourDefinition
        {
            Id = $"ellai-curated-{manifest.JourneyId}-{manifest.JourneyVersion}",
            Title = manifest.JourneyId,
            Steps = manifest.Steps.Select(s => ToStep(
                s.StepId, s.NarrationSource, ResolveTarget(s.ControlId, anchorByControlId), verifyCompletion)).ToList()
        };
    }

    private static BonboardingStepDefinition ToStep(string id, string text, BonboardingStepTarget target,
        Func<string, CancellationToken, ValueTask<bool>>? verifyCompletion, string? route = null) =>
        new()
        {
            Id = id,
            Title = text,
            Target = target,
            Route = route,
            MissingTarget = BonboardingMissingTargetBehavior.Wait,
            WaitTimeout = TimeSpan.FromSeconds(30),
            WaitTimeoutBehavior = BonboardingMissingTargetBehavior.Fail,
            CanAdvance = context => verifyCompletion is null
                ? ValueTask.FromResult(false)
                : verifyCompletion(id, context.CancellationToken),
            ValidationMessage = "Complete this step before continuing."
        };

    private static string? ResolveRoute(GuidancePlanStep step)
    {
        if (step.Behavior != PlanStepBehavior.RequestUiIntent) return null;
        return EllaiNavigationManifest.ResolveStepDestination(step)?.Route
            ?? throw new InvalidOperationException("The guidance navigation is not registered.");
    }

    private static BonboardingStepTarget ResolveTarget(
        string? controlId, IReadOnlyDictionary<string, string> anchorByControlId)
    {
        if (controlId is null) return BonboardingStepTarget.None;

        return BonboardingStepTarget.Dynamic(_ =>
        {
            if (!anchorByControlId.TryGetValue(controlId, out var anchor) || string.IsNullOrWhiteSpace(anchor))
                throw new InvalidOperationException("The guidance control no longer has a registered target.");
            return BonboardingStepTarget.Anchor(anchor);
        });
    }
}
