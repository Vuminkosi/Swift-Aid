using SwiftAid.Contracts.Commands;

namespace SwiftAid.Contracts.Cases;

/// <summary>
/// Staff case-command input matching the version-one command wire fields.
/// Only routing fields travel here; authorization always derives from the
/// server-authenticated principal.
/// </summary>
/// <param name="CommandType">One of CorrectReport, RequestProtectedAction, ReviewProtectedAction, Resolve, Reopen, RecordObservation, ScheduleFollowUp, AcknowledgeFollowUp, RecordFollowUpOutcome, RaiseEscalation, AcknowledgeEscalation, RecordEscalationAction, CloseEscalation, AcknowledgeAssignment, GrantCaseCollaborator, RevokeCaseCollaborator.</param>
/// <param name="Action">Protected-action name (for example Reassign); required by RequestProtectedAction.</param>
/// <param name="Reason">Operator reason; required except for AcknowledgeAssignment.</param>
/// <param name="ProposedOwnerId">Reassignment target, reopened-case owner, or accepted owner.</param>
/// <param name="RequestId">Protected-action request key.</param>
/// <param name="Outcome">Review outcome name (Pending, Denied, Approved); required by ReviewProtectedAction.</param>
/// <param name="ExpectedVersion">Caller concurrency version. Supplied values are preserved for corrections,
/// assignment acknowledgements and collaborator commands so retries keep their identity.</param>
/// <param name="ObservationId">Observation key; required by RecordObservation.</param>
/// <param name="Narrative">Observation narrative; required by RecordObservation.</param>
/// <param name="Source">Observation source; required by RecordObservation.</param>
/// <param name="FollowUpId">Follow-up key; required by ScheduleFollowUp, AcknowledgeFollowUp and RecordFollowUpOutcome.</param>
/// <param name="Purpose">Follow-up purpose; required by ScheduleFollowUp.</param>
/// <param name="AssigneeActorId">Follow-up assignee; required by ScheduleFollowUp.</param>
/// <param name="DueAtUtc">Follow-up due time; required by ScheduleFollowUp.</param>
/// <param name="OutcomeName">Follow-up outcome name (Completed, Missed, Unreachable, Rescheduled, Cancelled); required by RecordFollowUpOutcome.</param>
/// <param name="OutcomeNotes">Follow-up outcome notes; required by RecordFollowUpOutcome.</param>
/// <param name="NewDueAtUtc">Replacement due time; required by RecordFollowUpOutcome only when rescheduling.</param>
/// <param name="EscalationId">Escalation key; required by RaiseEscalation, AcknowledgeEscalation, CloseEscalation.</param>
/// <param name="RecipientRoute">Configured escalation recipient route; required by RaiseEscalation. Never a default.</param>
/// <param name="Resolution">Escalation resolution; required by CloseEscalation.</param>
/// <param name="ActionSummary">Optional legacy action summary on acknowledgement; required for RecordEscalationAction.</param>
/// <param name="EvidenceEventIds">Escalation evidence references for RaiseEscalation.</param>
/// <param name="AssignmentVersion">Observed assignment event version; required by AcknowledgeAssignment and preserved on retry.</param>
/// <param name="GrantId">Stable collaborator grant identity; required for grant and revoke.</param>
/// <param name="CollaboratorActorId">Explicit recipient; required for grant.</param>
/// <param name="CollaborationActions">Explicit action bitmask; required for grant and validated by the server.</param>
/// <param name="ExpiresAtUtc">Explicit UTC grant expiry; required for grant.</param>
public sealed record CaseCommandInput(
    string CommandType,
    string? Action,
    string? Reason,
    string? ProposedOwnerId,
    Guid? RequestId,
    string? Outcome,
    long? ExpectedVersion,
    Guid? ObservationId = null,
    string? Narrative = null,
    string? Source = null,
    Guid? FollowUpId = null,
    string? Purpose = null,
    string? AssigneeActorId = null,
    DateTimeOffset? DueAtUtc = null,
    string? OutcomeName = null,
    string? OutcomeNotes = null,
    DateTimeOffset? NewDueAtUtc = null,
    Guid? EscalationId = null,
    string? RecipientRoute = null,
    string? Resolution = null,
    string? ActionSummary = null,
    IReadOnlyList<string>? EvidenceEventIds = null,
    Guid? CommandId = null,
    long? AssignmentVersion = null,
    Guid? GrantId = null,
    string? CollaboratorActorId = null,
    CaseCollaborationActions? CollaborationActions = null,
    DateTimeOffset? ExpiresAtUtc = null,
    Guid? AssistanceId = null,
    string? AssistanceNeed = null,
    string? ResponsibleActorId = null,
    AssistanceStatus? AssistanceStatus = null,
    string? AssistanceNote = null,
    string? ReceiptMethod = null,
    string? ReceiptContext = null,
    ActionOwnerKind? ActionOwnerKind = null,
    Guid? ActionId = null);

/// <summary>Staff case-command outcome. Denial and absence share one value for privacy.</summary>
public enum CaseCommandStatus
{
    Accepted,
    Replayed,
    Conflict,
    Denied,
    Invalid,
    Unavailable
}

/// <summary>Safe command result. It contains no payload, grant group, or internal exception.</summary>
public sealed record CaseCommandResult(
    CaseCommandStatus Status,
    Guid CommandId,
    IReadOnlyList<Guid> EventIds,
    long? AggregateVersion,
    long? ReportRevision);

/// <summary>Staff case-command client. Servers enforce authorization; clients only render.</summary>
public interface ICaseCommandClient
{
    Task<CaseCommandResult> SendAsync(Guid caseId, CaseCommandInput input, CancellationToken cancellationToken = default);
}
