namespace SwiftAid.Application.Authorization;

public interface ICaseAccessPolicy
{
    ValueTask<AccessDecision> AuthorizeAsync(
        ActorContext actor,
        Guid caseId,
        CaseOperation operation,
        CancellationToken cancellationToken);

    ValueTask<AccessDecision> AuthorizeAllAsync(
        ActorContext actor,
        Guid caseId,
        IReadOnlyCollection<CaseOperation> requiredOperations,
        CancellationToken cancellationToken);
}

public enum AccessDecisionCode
{
    Allowed,
    InvalidResourceId,
    GrantMissing,
    GrantSourceUnavailable,
    GrantActorMismatch,
    AccessEpochMismatch,
    GrantExpired,
    ResourceMissing,
    ResourceSourceUnavailable,
    ResourceIdMismatch,
    OrganizationOutsideGrant,
    WorkspaceOutsideGrant,
    CountryOutsideGrant,
    RegionOutsideGrant,
    SectorOutsideGrant,
    SensitivityOutsideGrant,
    OperationNotGranted,
    CaseNotAssignedOrShared
}

public sealed record AccessDecision(
    bool IsAllowed,
    AccessDecisionCode Code,
    AccessAuditRecord? AuditRecord)
{
    /// <summary>The opaque group that authorized the complete decision, if allowed.</summary>
    public string? MatchedGrantGroupId { get; init; }
}
