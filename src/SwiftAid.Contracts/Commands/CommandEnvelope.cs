namespace SwiftAid.Contracts.Commands;

/// <summary>A typed, idempotent request to append one or more case events.</summary>
public sealed record CommandEnvelope<TPayload>(
    int SchemaVersion,
    Guid CommandId,
    Guid CaseId,
    long ExpectedVersion,
    string CommandType,
    DateTimeOffset? OccurredAtUtc,
    string? Reason,
    TPayload Payload);

public abstract record CaseCommand(Guid CommandId, long ExpectedVersion, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc);

public sealed record AcknowledgeAssignmentCommand(Guid CommandId, long ExpectedVersion, long AssignmentVersion,
    DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

public sealed record AcceptCaseCommand(
    Guid CommandId,
    Guid CaseId,
    ReportFacts Report,
    string AccountableOwnerId,
    string WorkspaceId,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, 0, OccurredAtUtc, RecordedAtUtc);

public sealed record CorrectReportCommand(
    Guid CommandId,
    long ExpectedVersion,
    string CorrectedNarrative,
    string Reason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

public sealed record RequestCaseActionCommand(
    Guid CommandId,
    long ExpectedVersion,
    Guid RequestId,
    CaseAction Action,
    string Reason,
    IReadOnlyList<string> EvidenceEventIds,
    string? ProposedOwnerId,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

public sealed record ReviewCaseActionRequestCommand(
    Guid CommandId,
    long ExpectedVersion,
    Guid RequestId,
    CaseActionReviewOutcome Outcome,
    string? AcceptedOwnerId,
    string Reason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

public sealed record ResolveCaseCommand(Guid CommandId, long ExpectedVersion, string Reason, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

public sealed record ReopenCaseCommand(
    Guid CommandId,
    long ExpectedVersion,
    string Reason,
    string AccountableOwnerId,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

/// <summary>Staff-recorded factual observation appended to an active case. Never a clinical assessment.</summary>
public sealed record RecordObservationCommand(
    Guid CommandId,
    long ExpectedVersion,
    Guid ObservationId,
    string Narrative,
    string Source,
    string Reason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

/// <summary>Schedules follow-up work with an eligible assignee and an actor-chosen due time.</summary>
public sealed record ScheduleFollowUpCommand(
    Guid CommandId,
    long ExpectedVersion,
    Guid FollowUpId,
    string Purpose,
    string AssigneeActorId,
    DateTimeOffset DueAtUtc,
    string Reason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

/// <summary>Terminal follow-up outcome. Missed and unreachable never imply care completed.</summary>
public enum FollowUpOutcome
{
    Completed,
    Missed,
    Unreachable,
    Rescheduled,
    Cancelled
}

/// <summary>Records the outcome of scheduled follow-up work. Rescheduling preserves the prior due time.</summary>
public sealed record RecordFollowUpOutcomeCommand(
    Guid CommandId,
    long ExpectedVersion,
    Guid FollowUpId,
    FollowUpOutcome Outcome,
    string OutcomeNotes,
    DateTimeOffset? NewDueAtUtc,
    string Reason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

/// <summary>Raises a staff-owned escalation concern on an active case. Never a clinical directive.</summary>
public sealed record RaiseEscalationCommand(
    Guid CommandId,
    long ExpectedVersion,
    Guid EscalationId,
    string Reason,
    IReadOnlyList<string> EvidenceEventIds,
    string RecipientRoute,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

/// <summary>Acknowledges a raised escalation. An unread notification never satisfies this.</summary>
public sealed record AcknowledgeEscalationCommand(
    Guid CommandId,
    long ExpectedVersion,
    Guid EscalationId,
    string? ActionSummary,
    string Reason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

/// <summary>Closes an acknowledged escalation with a recorded resolution.</summary>
public sealed record CloseEscalationCommand(
    Guid CommandId,
    long ExpectedVersion,
    Guid EscalationId,
    string Resolution,
    string Reason,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

public sealed record ReportFacts(CaseKind Kind, Sector Sector, string Narrative, IReadOnlyList<ReportedObservation> Observations);

public sealed record ReportedObservation(string Narrative);

public enum CaseKind
{
    Human,
    Animal
}

public enum Sector
{
    HumanHealth,
    AnimalHealth
}

public enum CaseAction
{
    Reassign,
    Resolve,
    Reopen,
    Assist
}

[Flags]
public enum CaseCapability
{
    AcceptReport = 1,
    AssignCase = 2,
    CorrectReport = 4,
    RequestProtectedAction = 8,
    ResolveCase = 16,
    ReopenCase = 32,
    ReviewProtectedAction = 64,
    RecordObservation = 128,
    ManageFollowUp = 256,
    ManageEscalation = 512,
    AcknowledgeAssignment = 1024,
    ManageCollaborators = 2048,
    ManageAssistance = 4096
}

public sealed record ActorCapabilities(string ActorId, CaseCapability Capabilities)
{
    public bool Has(CaseCapability capability) => Capabilities.HasFlag(capability);
}
