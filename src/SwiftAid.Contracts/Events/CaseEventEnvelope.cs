using SwiftAid.Contracts.Commands;

namespace SwiftAid.Contracts.Events;

public enum CaseEventType
{
    CaseReported,
    CaseAssigned,
    CaseReportCorrected,
    CaseActionRequested,
    CaseActionRequestReviewed,
    CaseResolved,
    CaseReopened
}

public abstract record CaseEventPayload;

public sealed record CaseReportPayload(
    CaseKind Kind,
    Sector Sector,
    string Narrative,
    IReadOnlyList<ReportedObservation> Observations);

public sealed record CaseReportedPayload(string WorkspaceId, CaseReportPayload Report) : CaseEventPayload;

public sealed record CaseAssignedPayload(string AccountableOwnerId) : CaseEventPayload;

public sealed record CaseReportCorrectedPayload(
    CaseReportPayload Report,
    string Reason,
    long PreviousReportRevision) : CaseEventPayload;

public sealed record CaseActionRequestedPayload(
    Guid RequestId,
    CaseAction RequestedOperation,
    string Reason,
    IReadOnlyList<string> EvidenceEventIds,
    string? ProposedOwnerId) : CaseEventPayload;

public sealed record CaseActionRequestReviewedPayload(
    Guid RequestId,
    CaseActionReviewOutcome Outcome,
    Guid? LinkedAcceptedEventId,
    string Reason) : CaseEventPayload;

public sealed record CaseResolvedPayload(string Reason) : CaseEventPayload;

public sealed record CaseReopenedPayload(string Reason, string AccountableOwnerId) : CaseEventPayload;

/// <summary>Versioned wire envelope for a single managed-case event.</summary>
public sealed record CaseEventEnvelope<TPayload>(
    int SchemaVersion,
    Guid EventId,
    CaseEventType EventType,
    Guid CaseId,
    long AggregateVersion,
    long ReportRevision,
    Guid CommandId,
    string ActorId,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    TPayload Payload) where TPayload : CaseEventPayload;
