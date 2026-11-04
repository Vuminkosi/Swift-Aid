namespace SwiftAid.Client.Presentation;

public enum OrganizationScenarioRole
{
    Member,
    Coordinator,
    Officer,
    Administrator
}

public enum OrganizationScenarioCountry
{
    Zimbabwe,
    Eswatini,
    Other
}

public enum OrganizationScenarioRegion
{
    Harare,
    Bulawayo,
    Manicaland,
    MashonalandCentral,
    MashonalandEast,
    MashonalandWest,
    Masvingo,
    MatabelelandNorth,
    MatabelelandSouth,
    Midlands,
    Hhohho,
    Lubombo,
    Manzini,
    Shiselweni,
    Other
}

public enum OrganizationScenarioAction
{
    UpdateOrganizationProfile,
    SubmitMembershipRequest,
    PreviewInvitation,
    CancelInvitationPreview,
    ApproveMembershipRequest,
    DeclineMembershipRequest,
    ChangeMemberRole,
    ChangeMemberRegion,
    RevokeMembership
}

public enum OrganizationScenarioMemberStatus
{
    Active,
    Pending,
    Revoked
}

public enum OrganizationScenarioRequestStatus
{
    Pending,
    Accepted,
    Declined
}

public enum OrganizationScenarioSsoState
{
    NotConfigured,
    SetupPending,
    VerificationRequired,
    Unavailable
}

public enum OrganizationScenarioAuditAction
{
    OrganizationProfileUpdated,
    MembershipRequestSubmitted,
    MembershipRequestAccepted,
    MembershipRequestDeclined,
    InvitationPreviewCreated,
    InvitationPreviewCancelled,
    MemberRoleProposed,
    MemberRegionProposed,
    MembershipRevocationProposed
}

public sealed record OrganizationScenarioCommand(
    long ExpectedVersion,
    OrganizationScenarioAction Action,
    string? SubjectReference = null,
    string? DisplayName = null,
    string? Email = null,
    OrganizationScenarioRole? Role = null,
    OrganizationScenarioRegion? Region = null,
    string? Reason = null,
    OrganizationScenarioCountry? Country = null);

public sealed record OrganizationScenarioProfile(
    string DisplayName,
    OrganizationScenarioCountry Country,
    OrganizationScenarioRegion Region,
    string Status);

public sealed record OrganizationScenarioMember(
    string ReferenceKey,
    string DisplayName,
    OrganizationScenarioRole Role,
    OrganizationScenarioRegion Region,
    OrganizationScenarioMemberStatus Status);

public sealed record OrganizationScenarioMembershipRequest(
    string ReferenceKey,
    string RequesterName,
    OrganizationScenarioCountry RequestedCountry,
    OrganizationScenarioRegion RequestedRegion,
    OrganizationScenarioRole RequestedRole,
    OrganizationScenarioRequestStatus Status,
    DateTimeOffset CreatedAtUtc);

public sealed record OrganizationScenarioInvitationPreview(
    string ReferenceKey,
    string Email,
    OrganizationScenarioRole Role,
    OrganizationScenarioRequestStatus Status,
    DateTimeOffset CreatedAtUtc);

public sealed record OrganizationScenarioProposal(
    string ReferenceKey,
    OrganizationScenarioAuditAction Action,
    string Target,
    string Summary,
    string Reason,
    DateTimeOffset CreatedAtUtc);

public sealed record OrganizationScenarioSsoStatus(
    OrganizationScenarioSsoState State,
    string Description,
    bool SecretStored);

public sealed record OrganizationScenarioRevocationSimulation(
    string ReferenceKey,
    string MemberReference,
    string MemberDisplayName,
    string Reason,
    string Outcome,
    DateTimeOffset CreatedAtUtc);

public sealed record OrganizationScenarioAuditEntry(
    OrganizationScenarioAuditAction Action,
    DateTimeOffset OccurredAtUtc);

public sealed record OrganizationScenarioSnapshot(
    string ScenarioId,
    long Version,
    string OrganizationName,
    OrganizationScenarioCountry Country,
    OrganizationScenarioRegion Region,
    OrganizationScenarioSsoState SsoState,
    IReadOnlyList<OrganizationScenarioRegion> AvailableRegions,
    IReadOnlyList<OrganizationScenarioRole> AvailableRoles,
    IReadOnlyList<OrganizationScenarioMember> Members,
    IReadOnlyList<OrganizationScenarioMembershipRequest> MembershipRequests,
    IReadOnlyList<OrganizationScenarioInvitationPreview> Invitations,
    IReadOnlyList<OrganizationScenarioProposal> Proposals,
    OrganizationScenarioSsoStatus SsoStatus,
    IReadOnlyList<OrganizationScenarioRevocationSimulation> RevocationSimulations,
    IReadOnlyList<OrganizationScenarioAuditEntry> Audit,
    bool IsSynthetic,
    string Notice);

/// <summary>Runs isolated organization examples without changing identities, grants, or provider configuration.</summary>
public interface IOrganizationScenarioClient
{
    Task<UiCapabilityResult<OrganizationScenarioSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationScenarioSnapshot>> ExecuteAsync(
        OrganizationScenarioCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed production implementation for the isolated organization scenario surface.</summary>
public sealed class UnavailableOrganizationScenarioClient : IOrganizationScenarioClient
{
    public Task<UiCapabilityResult<OrganizationScenarioSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<OrganizationScenarioSnapshot>("organization-scenario"));
    }

    public Task<UiCapabilityResult<OrganizationScenarioSnapshot>> ExecuteAsync(
        OrganizationScenarioCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);
        return Task.FromResult(UiCapabilityResults.Unavailable<OrganizationScenarioSnapshot>("organization-scenario"));
    }
}
