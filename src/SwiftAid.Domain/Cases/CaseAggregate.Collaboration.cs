using SwiftAid.Contracts.Commands;

namespace SwiftAid.Domain.Cases;

public sealed record CaseCollaboratorGranted(Guid CaseId, long AggregateVersion, long ReportRevision,
    Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc,
    Guid GrantId, string CollaboratorActorId, CaseCollaborationActions Actions, DateTimeOffset ExpiresAtUtc,
    string Reason) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);

public sealed record CaseCollaboratorRevoked(Guid CaseId, long AggregateVersion, long ReportRevision,
    Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc,
    Guid GrantId, string Reason) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);

public sealed record CaseCollaboratorState(Guid GrantId, string ActorId, CaseCollaborationActions Actions,
    DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc);

public sealed partial class CaseAggregate
{
    private readonly List<CaseCollaboratorState> _collaborators = [];
    public IReadOnlyList<CaseCollaboratorState> Collaborators => _collaborators.AsReadOnly();

    private IReadOnlyList<CaseEvent> DecideGrantCollaborator(GrantCaseCollaboratorCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ManageCollaborators);
        ValidateCollaboratorGrant(command.GrantId, command.CollaboratorActorId, command.Actions,
            command.ExpiresAtUtc, command.RecordedAtUtc, command.Reason);
        return [new CaseCollaboratorGranted(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
            command.OccurredAtUtc, command.RecordedAtUtc, command.GrantId, command.CollaboratorActorId,
            command.Actions, command.ExpiresAtUtc, command.Reason)];
    }

    private IReadOnlyList<CaseEvent> DecideRevokeCollaborator(RevokeCaseCollaboratorCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ManageCollaborators);
        RequireText(command.Reason, nameof(command.Reason));
        RequireRevocableGrant(command.GrantId);
        return [new CaseCollaboratorRevoked(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
            command.OccurredAtUtc, command.RecordedAtUtc, command.GrantId, command.Reason)];
    }

    private void ValidateCollaboratorGrant(Guid grantId, string actorId, CaseCollaborationActions actions,
        DateTimeOffset expiresAt, DateTimeOffset recordedAt, string reason)
    {
        const CaseCollaborationActions allowed = CaseCollaborationActions.ReadCase | CaseCollaborationActions.ReadAttachment |
            CaseCollaborationActions.Synchronize | CaseCollaborationActions.RecordObservation |
            CaseCollaborationActions.ManageFollowUp | CaseCollaborationActions.ManageEscalation;
        RequireText(actorId, nameof(actorId));
        RequireText(reason, nameof(reason));
        if (grantId == Guid.Empty || (actions & ~allowed) != 0 || !actions.HasFlag(CaseCollaborationActions.ReadCase))
            throw new ArgumentException("A collaborator grant requires an identifier and explicit supported actions including case read.");
        if (expiresAt.Offset != TimeSpan.Zero || expiresAt <= recordedAt)
            throw new ArgumentException("A collaborator grant requires a future UTC expiry.");
        if (Workflow == CaseWorkflow.Resolved || _collaborators.Any(item => item.GrantId == grantId ||
            (item.ActorId == actorId && item.RevokedAtUtc is null && item.ExpiresAtUtc > recordedAt)))
            throw new InvalidOperationException("The collaborator grant conflicts with the current case or grant state.");
    }

    private int RequireRevocableGrant(Guid grantId)
    {
        var index = _collaborators.FindIndex(item => item.GrantId == grantId && item.RevokedAtUtc is null);
        if (index < 0) throw new InvalidOperationException("The collaborator grant is absent or already revoked.");
        return index;
    }

    private void ApplyCollaboratorGrant(CaseCollaboratorGranted grant)
    {
        if (grant.ReportRevision != ReportRevision) throw new InvalidDataException("The collaborator grant has a stale report revision.");
        try
        {
            ValidateCollaboratorGrant(grant.GrantId, grant.CollaboratorActorId, grant.Actions, grant.ExpiresAtUtc, grant.RecordedAtUtc, grant.Reason);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The collaborator grant event is invalid.", exception);
        }
        _collaborators.Add(new(grant.GrantId, grant.CollaboratorActorId, grant.Actions, grant.ExpiresAtUtc, null));
    }

    private void ApplyCollaboratorRevocation(CaseCollaboratorRevoked revoke)
    {
        if (revoke.ReportRevision != ReportRevision) throw new InvalidDataException("The collaborator revocation has a stale report revision.");
        try
        {
            RequireText(revoke.Reason, nameof(revoke.Reason));
            var index = RequireRevocableGrant(revoke.GrantId);
            _collaborators[index] = _collaborators[index] with { RevokedAtUtc = revoke.RecordedAtUtc };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The collaborator revocation event is invalid.", exception);
        }
    }
}
