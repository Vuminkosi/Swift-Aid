using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Runtime discovery seam: converts a permission-filtered registry
/// snapshot plus the presentation layer's mounted-control report into
/// timestamped observations. Controls the snapshot entitles but the view
/// does not report are Unmounted — never assumed Ready.</summary>
public interface IEllaiUiDiscoveryService
{
    IReadOnlyList<UiControlObservation> Observe(
        UiRegistrySnapshot snapshot,
        IReadOnlySet<string> mountedControlIds,
        string viewInstanceId,
        string contextStamp,
        string platform,
        DateTimeOffset now,
        IReadOnlyList<UiControlState>? controlStates = null);
}

/// <summary>Planner seam: composes registry descriptors plus live
/// observations into a GuidancePlanV1 draft. The application validator
/// (not the planner) decides whether the draft becomes eligible.</summary>
public interface IEllaiGuidancePlanner
{
    EllaiGuidancePlanDraft ComposeDraft(
        string objective,
        GuidanceMode mode,
        UiRegistrySnapshot snapshot,
        IReadOnlyList<UiControlObservation> observations,
        int maxSteps);
}

/// <summary>Default discovery: Ready only for entitled controls the view
/// reports mounted; everything else snapshot-entitled is Unmounted.</summary>
public sealed class EllaiUiDiscoveryService : IEllaiUiDiscoveryService
{
    public IReadOnlyList<UiControlObservation> Observe(
        UiRegistrySnapshot snapshot,
        IReadOnlySet<string> mountedControlIds,
        string viewInstanceId,
        string contextStamp,
        string platform,
        DateTimeOffset now,
        IReadOnlyList<UiControlState>? controlStates = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mountedControlIds);
        return snapshot.Descriptors
            .Select(d => new UiControlObservation(
                d.ControlId, d.DescriptorVersion, viewInstanceId, contextStamp,
                ResolveState(d.ControlId, mountedControlIds, controlStates),
                platform, now, null))
            .ToList();
    }

    private static UiObservationState ResolveState(string controlId, IReadOnlySet<string> mounted,
        IReadOnlyList<UiControlState>? states)
    {
        if (!mounted.Contains(controlId)) return UiObservationState.Unmounted;
        var matches = states?.Where(s => s.ControlId == controlId).ToArray();
        if (matches is null || matches.Length == 0) return UiObservationState.Unknown;
        if (matches.Length != 1) return UiObservationState.Ambiguous;
        return Enum.IsDefined(matches[0].State) ? matches[0].State : UiObservationState.Unknown;
    }
}

/// <summary>Deterministic draft composer (ELL-T35 runtime). Steps target
/// Ready, non-human-owned controls only, in snapshot order; unknown goals
/// yield NeedsInput with a clarifying question instead of a fabricated plan.</summary>
public sealed class EllaiGuidancePlanner : IEllaiGuidancePlanner
{
    public EllaiGuidancePlanDraft ComposeDraft(
        string objective,
        GuidanceMode mode,
        UiRegistrySnapshot snapshot,
        IReadOnlyList<UiControlObservation> observations,
        int maxSteps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(observations);
        if (maxSteps is < 1 or > EllaiGuidancePlanDraft.MaxStepsPerChunk)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSteps));
        }

        var ready = observations
            .Where(o => o.State == UiObservationState.Ready)
            .ToDictionary(o => o.ControlId, StringComparer.Ordinal);
        var steps = snapshot.Descriptors
            .Where(d => ready.ContainsKey(d.ControlId))
            .Where(d => d.RiskClass is not UiRiskClass.R3 and not UiRiskClass.R5)
            .Take(maxSteps)
            .Select((d, i) => new GuidancePlanStep(
                $"s{i + 1}", d.ControlId, d.ObjectiveId ?? d.ControlId,
                PlanStepBehavior.Explain, null, null,
                d.ProductFactIds, Array.Empty<string>(),
                $"See {d.ControlId}.",
                null))
            .ToList();

        if (steps.Count == 0)
        {
            return new EllaiGuidancePlanDraft(
                EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.NeedsInput,
                objective, mode, Array.Empty<GuidancePlanStep>(),
                new[] { new EllaiQuestion("q1", "Which part of the screen should I explain first?", Array.Empty<string>()) },
                "No ready entitled control matches the current view.");
        }

        for (var i = 0; i < steps.Count - 1; i++)
        {
            steps[i] = steps[i] with { NextStepKey = steps[i + 1].StepKey };
        }

        return new EllaiGuidancePlanDraft(
            EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.Ready,
            objective, mode, steps, Array.Empty<EllaiQuestion>(), null);
    }
}
