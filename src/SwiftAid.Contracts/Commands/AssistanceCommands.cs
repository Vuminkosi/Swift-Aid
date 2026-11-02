namespace SwiftAid.Contracts.Commands;

public enum AssistanceStatus { Requested, Accepted, InProgress, Provided, ReceiptConfirmed, Unfulfilled, Cancelled }

public sealed record RequestAssistanceCommand(Guid CommandId, long ExpectedVersion, Guid AssistanceId,
    string Need, string ResponsibleActorId, DateTimeOffset? DueAtUtc,
    DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

/// <summary>Records human activity. Receipt method/context describe evidence, not independent verification.</summary>
public sealed record UpdateAssistanceCommand(Guid CommandId, long ExpectedVersion, Guid AssistanceId,
    AssistanceStatus Status, string Note, string? ReceiptMethod, string? ReceiptContext,
    DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);
