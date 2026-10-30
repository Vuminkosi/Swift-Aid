using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Start request for a curated journey (creation-only; control uses
/// GuidanceControlRequest).</summary>
public sealed record StartGuidanceRequest(
    string ClientCommandId,
    string JourneyId,
    string JourneyVersion,
    GuidanceMode Mode,
    string? StartStepId);

/// <summary>Revision activation request for an accepted generated plan.</summary>
public sealed record ActivateGuidanceRevisionRequest(
    string ClientCommandId,
    string GuidanceAttemptId,
    long ExpectedGuidanceVersion,
    ValidatedGuidancePlan Plan);

/// <summary>Runtime observation from the presentation layer.</summary>
public sealed record GuidanceRuntimeEvent(
    string EventId,
    string GuidanceAttemptId,
    long GuidanceVersion,
    long Sequence,
    string Kind,
    string Observation,
    DateTimeOffset OccurredAtUtc);

/// <summary>Single active guidance controller with serialized commands,
/// fencing versions and immutable revision activation (ELL-T27/37 groundwork).
/// Terminal sessions never resume; cancelled parents reject all commands.</summary>
public sealed class EllaiGuidanceSessionCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CoordinatorState> _attempts = new(StringComparer.Ordinal);

    private sealed record CoordinatorState(
        GuidancePosition Position,
        GuidanceStatus Status,
        ValidatedGuidancePlan? ActivePlan,
        long LastCommandSequence,
        long LastEventSequence);

    public GuidanceSnapshot Start(StartGuidanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attemptId = $"ga-{Guid.NewGuid():N}";
        var position = new GuidancePosition(
            attemptId, request.JourneyId, request.JourneyVersion,
            $"manifest-{request.JourneyId}-{request.JourneyVersion}",
            request.StartStepId ?? "step-1", $"visit-{Guid.NewGuid():N}",
            1, 1, 1, 1);
        var state = new CoordinatorState(position, GuidanceStatus.Showing, null, 1, 0);
        lock (_gate)
        {
            _attempts[attemptId] = state;
        }

        return ToSnapshot(state, request.Mode);
    }

    public GuidanceSnapshot? Dispatch(GuidanceControlRequest request, GuidanceMode mode)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_attempts.TryGetValue(request.GuidanceAttemptId, out var state))
            {
                return null;
            }

            if (state.Status is GuidanceStatus.Cancelled or GuidanceStatus.Completed or GuidanceStatus.Failed)
            {
                return null;
            }

            if (state.Position.GuidanceVersion != request.ExpectedGuidanceVersion)
            {
                return ToSnapshot(state with { Status = GuidanceStatus.Stale }, mode);
            }

            if (request.Command == GuidanceCommandKind.Jump && request.TargetStepId is null)
            {
                return ToSnapshot(state with { Status = GuidanceStatus.Blocked }, mode);
            }

            if (request.Command == GuidanceCommandKind.SwitchMode && request.TargetMode is null)
            {
                return ToSnapshot(state with { Status = GuidanceStatus.Blocked }, mode);
            }

            var next = request.Command switch
            {
                GuidanceCommandKind.Pause => state with { Status = GuidanceStatus.Paused },
                GuidanceCommandKind.Resume => state with { Status = GuidanceStatus.Showing },
                GuidanceCommandKind.Stop => state with { Status = GuidanceStatus.Cancelled },
                _ => state with
                {
                    Position = state.Position with
                    {
                        GuidanceVersion = state.Position.GuidanceVersion + 1,
                        InteractionEpoch = state.Position.InteractionEpoch + 1
                    },
                    LastCommandSequence = state.LastCommandSequence + 1
                }
            };
            _attempts[request.GuidanceAttemptId] = next;
            return ToSnapshot(next, request.TargetMode ?? mode);
        }
    }

    public GuidanceSnapshot? ApplyRevision(ActivateGuidanceRevisionRequest request, GuidanceMode mode)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        lock (_gate)
        {
            if (!_attempts.TryGetValue(request.GuidanceAttemptId, out var state))
            {
                return null;
            }

            if (state.Status is GuidanceStatus.Cancelled or GuidanceStatus.Completed or GuidanceStatus.Failed)
            {
                return null;
            }

            if (state.Position.GuidanceVersion != request.ExpectedGuidanceVersion)
            {
                return ToSnapshot(state with { Status = GuidanceStatus.Stale }, mode);
            }

            var firstStep = request.Plan.Steps.Count > 0 ? request.Plan.Steps[0].StepKey : state.Position.StepId;
            var next = state with
            {
                Position = state.Position with
                {
                    GuidanceVersion = state.Position.GuidanceVersion + 1,
                    InteractionEpoch = state.Position.InteractionEpoch + 1,
                    SpeechEpoch = state.Position.SpeechEpoch + 1,
                    StepId = firstStep,
                    StepVisitId = $"visit-{Guid.NewGuid():N}",
                    GeneratedPlanId = request.Plan.PlanId,
                    GeneratedPlanRevision = request.Plan.Revision
                },
                ActivePlan = request.Plan,
                Status = GuidanceStatus.Showing,
                LastCommandSequence = state.LastCommandSequence + 1
            };
            _attempts[request.GuidanceAttemptId] = next;
            return ToSnapshot(next, mode);
        }
    }

    private static GuidanceSnapshot ToSnapshot(CoordinatorState state, GuidanceMode mode) => new(
        state.Position, mode, state.Status, null, null,
        state.Status is GuidanceStatus.Cancelled or GuidanceStatus.Completed or GuidanceStatus.Failed
            ? Array.Empty<string>()
            : new[] { "Next", "Previous", "Pause", "Stop", "Return" },
        "Ready", "Segment 1 of unknown total", "No task proof yet", "No effects",
        state.LastCommandSequence, state.LastEventSequence);
}
