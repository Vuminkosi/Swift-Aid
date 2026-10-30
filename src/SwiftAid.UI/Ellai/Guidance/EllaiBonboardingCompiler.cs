using Bit.Bonboarding;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Deterministic compiler: accepted plans and curated manifests become
/// trusted <see cref="BonboardingTourDefinition"/> instances with safe callback
/// mappings. No model calls, navigation, speech, code execution or protected
/// writes. Targets resolve via <see cref="BonboardingStepTarget.Anchor"/> from
/// the trusted anchor map, or <see cref="BonboardingStepTarget.None"/> for
/// purely informational steps.</summary>
public sealed class EllaiBonboardingCompiler
{
    public BonboardingTourDefinition Compile(
        ValidatedGuidancePlan plan, IReadOnlyDictionary<string, string> anchorByControlId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(anchorByControlId);

        return new BonboardingTourDefinition
        {
            Id = $"ellai-generated-{plan.PlanId}-r{plan.Revision}",
            Title = "Ellai guidance",
            Steps = plan.Steps.Select(s => ToStep(
                s.StepKey, s.Explanation, ResolveTarget(s.ControlId, anchorByControlId))).ToList()
        };
    }

    public BonboardingTourDefinition CompileManifest(
        EllaiJourneyManifest manifest, IReadOnlyDictionary<string, string> anchorByControlId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(anchorByControlId);

        return new BonboardingTourDefinition
        {
            Id = $"ellai-curated-{manifest.JourneyId}-{manifest.JourneyVersion}",
            Title = manifest.JourneyId,
            Steps = manifest.Steps.Select(s => ToStep(
                s.StepId, s.NarrationSource, ResolveTarget(s.ControlId, anchorByControlId))).ToList()
        };
    }

    private static BonboardingStepDefinition ToStep(string id, string text, BonboardingStepTarget target) =>
        new()
        {
            Id = id,
            Title = text,
            Target = target
        };

    private static BonboardingStepTarget ResolveTarget(
        string? controlId, IReadOnlyDictionary<string, string> anchorByControlId)
    {
        if (controlId is not null && anchorByControlId.TryGetValue(controlId, out var anchor))
        {
            return BonboardingStepTarget.Anchor(anchor);
        }

        return BonboardingStepTarget.None;
    }
}
