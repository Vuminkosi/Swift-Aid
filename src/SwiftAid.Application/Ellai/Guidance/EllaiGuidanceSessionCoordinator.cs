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

public sealed class EllaiGuidanceSessionCoordinator
{
    private readonly GuidanceSessionState _state = new();

    public GuidanceSnapshot Start(StartGuidanceRequest request) =>
        _state.Start(new(request.ClientCommandId, request.JourneyId, request.JourneyVersion, request.Mode, request.StartStepId));

    public GuidanceSnapshot? Dispatch(GuidanceControlRequest request, GuidanceMode mode) =>
        _state.Dispatch(request, mode);

    public GuidanceSnapshot? ApplyRevision(ActivateGuidanceRevisionRequest request, GuidanceMode mode) =>
        _state.ApplyRevision(new(request.ClientCommandId, request.GuidanceAttemptId, request.ExpectedGuidanceVersion, request.Plan), mode);
}