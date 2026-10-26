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
    Reopen
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
    ReviewProtectedAction = 64
}

public sealed record ActorCapabilities(string ActorId, CaseCapability Capabilities)
{
    public bool Has(CaseCapability capability) => Capabilities.HasFlag(capability);
}
