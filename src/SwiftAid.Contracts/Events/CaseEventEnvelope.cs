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
    CaseReopened,
    CaseObservationRecorded,
    CaseFollowUpScheduled,
    CaseFollowUpOutcomeRecorded,
    CaseEscalationRaised,
    CaseEscalationAcknowledged,
    CaseEscalationClosed,
    CaseAssignmentAcknowledged,
    CaseCollaboratorGranted,
    CaseCollaboratorRevoked,
    AssistanceRequested,
    AssistanceAccepted,
    AssistanceProgressRecorded,
    AssistanceProvided,
    AssistanceReceiptConfirmed,
    AssistanceUnfulfilled,
    AssistanceCancelled,
    CaseFollowUpAcknowledged,
    ActionOwnerChanged,
    CaseEscalationActionRecorded
}

public abstract record CaseEventPayload;

public sealed record AssistanceRequestedPayload(Guid AssistanceId, string Need, string ResponsibleActorId, DateTimeOffset? DueAtUtc) : CaseEventPayload;
public sealed record AssistanceActivityPayload(Guid AssistanceId, string Note) : CaseEventPayload;
public sealed record AssistanceReceiptPayload(Guid AssistanceId, string Note, string ReceiptMethod, string ReceiptContext) : CaseEventPayload;

public sealed record CaseReportPayload(
    CaseKind Kind,
    Sector Sector,
    string Narrative,
    IReadOnlyList<ReportedObservation> Observations);

public sealed record CaseReportedPayload(string WorkspaceId, CaseReportPayload Report) : CaseEventPayload;

public sealed record CaseAssignedPayload(string AccountableOwnerId) : CaseEventPayload;

public sealed record CaseAssignmentAcknowledgedPayload(long AssignmentVersion) : CaseEventPayload;

public sealed record CaseCollaboratorGrantedPayload(Guid GrantId, string CollaboratorActorId,
    CaseCollaborationActions Actions, DateTimeOffset ExpiresAtUtc, string Reason) : CaseEventPayload;

public sealed record CaseCollaboratorRevokedPayload(Guid GrantId, string Reason) : CaseEventPayload;

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

public sealed record CaseObservationRecordedPayload(Guid ObservationId, string Narrative, string Source) : CaseEventPayload;

public sealed record CaseFollowUpScheduledPayload(Guid FollowUpId, string Purpose, string AssigneeActorId, DateTimeOffset DueAtUtc) : CaseEventPayload;

public sealed record CaseFollowUpAcknowledgedPayload(Guid FollowUpId) : CaseEventPayload;

public sealed record CaseFollowUpOutcomeRecordedPayload(
    Guid FollowUpId,
    FollowUpOutcome Outcome,
    string OutcomeNotes,
    DateTimeOffset PreviousDueAtUtc,
    DateTimeOffset? NewDueAtUtc,
    string Reason) : CaseEventPayload;

public sealed record ActionOwnerChangedPayload(ActionOwnerKind ActionKind, Guid ActionId,
    string PreviousOwnerId, string NewOwnerId, string Reason) : CaseEventPayload;

public sealed record CaseEscalationRaisedPayload(
    Guid EscalationId,
    string Reason,
    IReadOnlyList<string> EvidenceEventIds,
    string RecipientRoute) : CaseEventPayload;

public sealed record CaseEscalationAcknowledgedPayload(Guid EscalationId, string? ActionSummary, string Reason) : CaseEventPayload;

public sealed record CaseEscalationActionRecordedPayload(Guid EscalationId, string ActionSummary, string Reason) : CaseEventPayload;

public sealed record CaseEscalationClosedPayload(Guid EscalationId, string Resolution, string Reason) : CaseEventPayload;

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
