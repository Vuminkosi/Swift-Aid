namespace SwiftAid.Contracts.Commands;

/// <summary>Explicit collaboration permissions; never grants ownership or permission administration.</summary>
[Flags]
public enum CaseCollaborationActions
{
    ReadCase = 1,
    ReadAttachment = 2,
    Synchronize = 4,
    RecordObservation = 8,
    ManageFollowUp = 16,
    ManageEscalation = 32
}

/// <summary>Grants bounded actions on this case; recipient sector and organization scope require server validation.</summary>
public sealed record GrantCaseCollaboratorCommand(Guid CommandId, long ExpectedVersion, Guid GrantId,
    string CollaboratorActorId, CaseCollaborationActions Actions, DateTimeOffset ExpiresAtUtc, string Reason,
    DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);

public sealed record RevokeCaseCollaboratorCommand(Guid CommandId, long ExpectedVersion, Guid GrantId, string Reason,
    DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc)
    : CaseCommand(CommandId, ExpectedVersion, OccurredAtUtc, RecordedAtUtc);
