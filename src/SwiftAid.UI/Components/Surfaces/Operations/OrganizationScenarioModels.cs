namespace SwiftAid.UI.Components.Surfaces.Operations;

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

public sealed record OrganizationScenarioMemberView(
    string ReferenceKey,
    string DisplayName,
    OrganizationScenarioRole Role,
    OrganizationScenarioRegion Region,
    OrganizationScenarioMemberStatus Status);

public sealed record OrganizationScenarioMembershipRequestView(
    string ReferenceKey,
    string RequesterName,
    OrganizationScenarioRole RequestedRole,
    OrganizationScenarioRegion RequestedRegion,
    OrganizationScenarioRequestStatus Status);

public sealed record OrganizationScenarioInvitationView(
    string ReferenceKey,
    string Email,
    OrganizationScenarioRole Role,
    OrganizationScenarioRequestStatus Status);

public sealed record OrganizationScenarioAuditEntry(
    OrganizationScenarioAuditAction Action,
    DateTimeOffset OccurredAtUtc);

/// <summary>Presentation-only organization fixture. Reference keys are never rendered as text.</summary>
public sealed record OrganizationScenarioData(
    string OrganizationName,
    OrganizationScenarioCountry Country,
    OrganizationScenarioRegion Region,
    OrganizationScenarioSsoState SsoState,
    IReadOnlyList<OrganizationScenarioRegion> AvailableRegions,
    IReadOnlyList<OrganizationScenarioRole> AvailableRoles,
    IReadOnlyList<OrganizationScenarioMemberView> Members,
    IReadOnlyList<OrganizationScenarioMembershipRequestView> MembershipRequests,
    IReadOnlyList<OrganizationScenarioInvitationView> Invitations,
    IReadOnlyList<OrganizationScenarioAuditEntry> Audit);

/// <summary>A closed scenario action request. The host must handle it without changing real access.</summary>
public sealed record OrganizationScenarioActionRequest(
    OrganizationScenarioAction Action,
    string? SubjectReference,
    string? DisplayName,
    string? Email,
    OrganizationScenarioRole? Role,
    OrganizationScenarioRegion? Region,
    string? Reason,
    OrganizationScenarioCountry? Country = null);
