using SwiftAid.Contracts.Commands;
using SwiftAid.Application.Authorization;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Infrastructure.Persistence;

public enum PersistentActorStatus
{
    Unspecified,
    Active,
    Suspended,
    Revoked
}

public enum PersistentResourceStatus
{
    Unspecified,
    Active,
    Suspended,
    Revoked
}

public enum PersistentMembershipStatus
{
    Unspecified,
    Active,
    Suspended,
    Revoked
}

public enum PersistentGrantStatus
{
    Unspecified,
    Active,
    Suspended,
    Revoked
}

public sealed class ActorEntity
{
    public required string ActorId { get; set; }
    public PersistentActorStatus Status { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ActorAccessStateEntity
{
    public required string ActorId { get; set; }
    public long AccessEpoch { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class OrganizationEntity
{
    public required string OrganizationId { get; set; }
    public PersistentResourceStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class WorkspaceEntity
{
    public required string WorkspaceId { get; set; }
    public required string OrganizationId { get; set; }
    public PersistentResourceStatus Status { get; set; }
}

/// <summary>Explicit participating organization; workspace sharing never implies case sharing.</summary>
public sealed class WorkspaceOrganizationEntity
{
    public required string WorkspaceId { get; set; }
    public required string OrganizationId { get; set; }
    public PersistentResourceStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class OrganizationMembershipEntity
{
    public Guid MembershipId { get; set; }
    public required string ActorId { get; set; }
    public required string OrganizationId { get; set; }
    public PersistentMembershipStatus Status { get; set; }
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public required string GrantedByActorId { get; set; }
    public string? RevokedByActorId { get; set; }
    public string? ChangeReason { get; set; }
    public long Version { get; set; }
}

/// <summary>One persisted scalar scope materialized as one correlated grant group.</summary>
public sealed class AccessGrantGroupEntity
{
    public required string GrantGroupId { get; set; }
    public Guid MembershipId { get; set; }
    public required string ActorId { get; set; }
    public required string OrganizationId { get; set; }
    public required string WorkspaceId { get; set; }
    public required string CountryCode { get; set; }
    public Sector Sector { get; set; }
    public required string SensitivityCode { get; set; }
    public bool CanAccessJurisdictionalCases { get; set; }
    public PersistentGrantStatus Status { get; set; }
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public required string GrantedByActorId { get; set; }
    public string? RevokedByActorId { get; set; }
    public string? ChangeReason { get; set; }
    public long Version { get; set; }
}

public sealed class AccessGrantRegionEntity
{
    public required string GrantGroupId { get; set; }
    public required string RegionId { get; set; }
}

public sealed class AccessGrantOperationEntity
{
    public required string GrantGroupId { get; set; }
    public CaseOperation Operation { get; set; }
}

public sealed class ManagedCaseEntity
{
    public Guid CaseId { get; set; }
    public required string OrganizationId { get; set; }
    public required string WorkspaceId { get; set; }
    public required string CountryCode { get; set; }
    public CaseKind Kind { get; set; }
    public Sector Sector { get; set; }
    public required string SensitivityCode { get; set; }
    public required string AccountableOwnerActorId { get; set; }
    public long ResourceVersion { get; set; }
    public long AggregateVersion { get; set; }
    public long ReportRevision { get; set; }
    public CaseWorkflow Workflow { get; set; }
    public long WorkflowVersion { get; set; }
    public ClinicalClassification Classification { get; set; }
    public long ClassificationVersion { get; set; }
    public LaboratoryState Laboratory { get; set; }
    public long LaboratoryVersion { get; set; }
    public ReportedValueState OutcomeState { get; set; }
    public string? OutcomeValue { get; set; }
    public long OutcomeVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ManagedCaseRegionEntity
{
    public Guid CaseId { get; set; }
    public required string RegionId { get; set; }
}

public sealed class CaseShareEntity
{
    public Guid? GrantId { get; set; }
    /// <summary>Null preserves legacy shares; explicit values bound a collaborator's actions.</summary>
    public string[]? AllowedOperations { get; set; }
    public Guid CaseId { get; set; }
    public required string ActorId { get; set; }
    public long Version { get; set; }
    public bool IsActive { get; set; }
    public required string GrantedByActorId { get; set; }
    public string? RevokedByActorId { get; set; }
    public string? ChangeReason { get; set; }
    public DateTimeOffset GrantedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

/// <summary>Data Protection ciphertext only; plaintext event payloads never enter SQL metadata.</summary>
public sealed class ProtectedEventPayloadEntity
{
    public Guid PayloadId { get; set; }
    public required byte[] ProtectedBytes { get; set; }
    public required string ProtectionPurposeVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CaseEventEntity
{
    public Guid EventId { get; set; }
    public Guid CaseId { get; set; }
    public long AggregateVersion { get; set; }
    public long ReportRevision { get; set; }
    public Guid CommandId { get; set; }
    public required string ActorId { get; set; }
    public required string EventType { get; set; }
    public int SchemaVersion { get; set; }
    public DateTimeOffset? OccurredAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public Guid PayloadId { get; set; }
}

public sealed class CommandReceiptEntity
{
    public Guid ReceiptId { get; set; }
    public required string ActorId { get; set; }
    public Guid CommandId { get; set; }
    public Guid CaseId { get; set; }
    public required string CommandType { get; set; }
    public int SchemaVersion { get; set; }
    public required byte[] CanonicalPayloadHash { get; set; }
    public long FirstAggregateVersion { get; set; }
    public long LastAggregateVersion { get; set; }
    public long ReportRevision { get; set; }
    public DateTimeOffset AcceptedAtUtc { get; set; }
}

public sealed class DomainOutboxEntity
{
    public Guid OutboxId { get; set; }
    public Guid EventId { get; set; }
    public required string MessageType { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? ExhaustedAtUtc { get; set; }
    public string? LastFailureCode { get; set; }
}

public sealed class SecurityAuditEntryEntity
{
    public Guid AuditId { get; set; }
    public required string ActorId { get; set; }
    public string? TargetActorId { get; set; }
    public Guid? ResourceId { get; set; }
    public required string Operation { get; set; }
    public required string DecisionCode { get; set; }
    public long AccessEpoch { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string? ReasonCode { get; set; }
}

/// <summary>Single active email-verification code per user. Codes are stored hashed, expire quickly and allow few attempts.</summary>
public sealed class EmailVerificationEntity
{
    public required string UserId { get; set; }

    public required string CodeHash { get; set; }

    public string? PendingEmail { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset LastSentAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum MembershipRequestStatus
{
    Unspecified,
    Pending,
    Approved,
    Declined
}

/// <summary>In-app notification preferences. Channel delivery consent stays with device settings.</summary>
public sealed class NotificationPreferenceEntity
{
    public required string UserId { get; set; }

    public bool WorkReminders { get; set; }

    public bool SessionUpdates { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Member-initiated request to join an organization. Review stays with staff tooling.</summary>
public sealed class MembershipRequestEntity
{
    public Guid RequestId { get; set; }

    public required string ActorId { get; set; }

    public required string OrganizationId { get; set; }

    public MembershipRequestStatus Status { get; set; }

    public string? Reason { get; set; }

    public Guid? InvitationId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class OrganizationInvitationEntity
{
    public Guid InvitationId { get; set; }

    public required string OrganizationId { get; set; }

    public required string CodeHash { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public int MaxUses { get; set; }

    public int UseCount { get; set; }

    public string? CreatedByActorId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Self-asserted profile fields. Identity owns the email; authorization owns membership.</summary>
public sealed class UserProfileEntity
{
    public required string UserId { get; set; }

    public string? DisplayName { get; set; }

    public string? Phone { get; set; }

    public string? Country { get; set; }

    public string? Region { get; set; }

    public string? Locality { get; set; }

    public Guid? AvatarObjectId { get; set; }

    public string? AvatarContentType { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
/// <summary>Canonical geographic registry record. Stable curated IDs; coordinates only when verified.</summary>
public sealed class GeographicLocationEntity
{
    public required string LocationId { get; set; }

    public required string CountryCode { get; set; }

    public required string Name { get; set; }

    public required string LocationKind { get; set; }

    public string? ParentId { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public required string Source { get; set; }

    public required string Verification { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; }
}
