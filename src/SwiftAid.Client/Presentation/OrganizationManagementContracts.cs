namespace SwiftAid.Client.Presentation;

/// <summary>Closed organization role values supported by the management UI.</summary>
public enum OrganizationRole
{
    Member,
    Coordinator,
    Officer,
    Administrator
}

public enum OrganizationCountry
{
    Zimbabwe,
    Eswatini,
    Other
}

/// <summary>Supported service regions. Region values are validated against their country.</summary>
public enum OrganizationRegion
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

public enum OrganizationMembershipRequestStatus
{
    Pending,
    Approved,
    Declined
}

public enum OrganizationSsoStatus
{
    NotConfigured,
    SetupPending,
    VerificationRequired,
    Unavailable
}

public enum OrganizationAuditAction
{
    ProfileUpdated,
    MembershipRequested,
    MembershipApproved,
    MembershipDeclined,
    InvitationPreviewCreated,
    InvitationPreviewCancelled,
    MemberRoleChanged,
    MemberRegionChanged,
    MembershipRevoked,
    SsoConfigurationChanged
}

public sealed record OrganizationProfile(
    string DisplayName,
    OrganizationCountry Country,
    OrganizationRegion Region);

public sealed record OrganizationProfileUpdate(
    string DisplayName,
    OrganizationCountry Country,
    OrganizationRegion Region,
    string Reason);

public sealed record OrganizationMembershipRequest(
    string Id,
    string RequesterName,
    OrganizationCountry Country,
    OrganizationRegion Region,
    OrganizationRole RequestedRole,
    OrganizationMembershipRequestStatus Status,
    DateTimeOffset CreatedAtUtc);

public sealed record OrganizationMembershipRequestSubmission(
    string RequesterName,
    OrganizationCountry Country,
    OrganizationRegion Region,
    OrganizationRole RequestedRole,
    string Reason);

public sealed record OrganizationInvitationRequest(
    string Email,
    OrganizationRole Role,
    OrganizationCountry Country,
    OrganizationRegion Region,
    string Reason);

public sealed record OrganizationMembershipRequestReview(
    string RequestId,
    bool Approve,
    string Reason);

public sealed record ManagedOrganizationMember(
    string Id,
    string DisplayName,
    OrganizationRole Role,
    OrganizationCountry Country,
    OrganizationRegion Region,
    string Status);

public sealed record OrganizationMemberChange(
    string MemberId,
    OrganizationRole? Role,
    OrganizationCountry? Country,
    OrganizationRegion? Region,
    string Reason);

public sealed record OrganizationMembershipRevocation(
    string MemberId,
    string Reason);

public sealed record OrganizationSsoConfiguration(
    OrganizationSsoStatus Status,
    string Description,
    bool HasStoredSecret);

public sealed record OrganizationAuditEntry(
    OrganizationAuditAction Action,
    string Summary,
    DateTimeOffset OccurredAtUtc);

/// <summary>Typed organization-management operations not currently present in IOrganizationClient.</summary>
public interface IOrganizationManagementClient
{
    Task<UiCapabilityResult<OrganizationProfile>> GetProfileAsync(CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationProfile>> UpdateProfileAsync(
        OrganizationProfileUpdate update,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<Page<OrganizationMembershipRequest>>> ListMembershipRequestsAsync(
        PageRequest page,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<Page<ManagedOrganizationMember>>> ListManagedMembersAsync(
        PageRequest page,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationMembershipRequest>> SubmitMembershipRequestAsync(
        OrganizationMembershipRequestSubmission request,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationMembershipRequest>> ReviewMembershipRequestAsync(
        OrganizationMembershipRequestReview review,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationInvitation>> SendInvitationAsync(
        OrganizationInvitationRequest invitation,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<ManagedOrganizationMember>> UpdateMemberAsync(
        OrganizationMemberChange change,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<ManagedOrganizationMember>> RevokeMembershipAsync(
        OrganizationMembershipRevocation revocation,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationSsoConfiguration>> GetSsoConfigurationAsync(
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationSsoConfiguration>> RequestSsoSetupAsync(
        string reason,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<IReadOnlyList<OrganizationAuditEntry>>> GetAuditAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed implementation for environments without organization-management services.</summary>
public sealed class UnavailableOrganizationManagementClient : IOrganizationManagementClient
{
    public Task<UiCapabilityResult<OrganizationProfile>> GetProfileAsync(CancellationToken cancellationToken = default) =>
        Unavailable<OrganizationProfile>(cancellationToken);

    public Task<UiCapabilityResult<OrganizationProfile>> UpdateProfileAsync(
        OrganizationProfileUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return Unavailable<OrganizationProfile>(cancellationToken);
    }

    public Task<UiCapabilityResult<Page<OrganizationMembershipRequest>>> ListMembershipRequestsAsync(
        PageRequest page, CancellationToken cancellationToken = default) =>
        Unavailable<Page<OrganizationMembershipRequest>>(cancellationToken);

    public Task<UiCapabilityResult<Page<ManagedOrganizationMember>>> ListManagedMembersAsync(
        PageRequest page, CancellationToken cancellationToken = default) =>
        Unavailable<Page<ManagedOrganizationMember>>(cancellationToken);

    public Task<UiCapabilityResult<OrganizationMembershipRequest>> SubmitMembershipRequestAsync(
        OrganizationMembershipRequestSubmission request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Unavailable<OrganizationMembershipRequest>(cancellationToken);
    }

    public Task<UiCapabilityResult<OrganizationMembershipRequest>> ReviewMembershipRequestAsync(
        OrganizationMembershipRequestReview review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        return Unavailable<OrganizationMembershipRequest>(cancellationToken);
    }

    public Task<UiCapabilityResult<OrganizationInvitation>> SendInvitationAsync(
        OrganizationInvitationRequest invitation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return Unavailable<OrganizationInvitation>(cancellationToken);
    }

    public Task<UiCapabilityResult<ManagedOrganizationMember>> UpdateMemberAsync(
        OrganizationMemberChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Unavailable<ManagedOrganizationMember>(cancellationToken);
    }

    public Task<UiCapabilityResult<ManagedOrganizationMember>> RevokeMembershipAsync(
        OrganizationMembershipRevocation revocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        return Unavailable<ManagedOrganizationMember>(cancellationToken);
    }

    public Task<UiCapabilityResult<OrganizationSsoConfiguration>> GetSsoConfigurationAsync(CancellationToken cancellationToken = default) =>
        Unavailable<OrganizationSsoConfiguration>(cancellationToken);

    public Task<UiCapabilityResult<OrganizationSsoConfiguration>> RequestSsoSetupAsync(
        string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return Unavailable<OrganizationSsoConfiguration>(cancellationToken);
    }

    public Task<UiCapabilityResult<IReadOnlyList<OrganizationAuditEntry>>> GetAuditAsync(CancellationToken cancellationToken = default) =>
        Unavailable<IReadOnlyList<OrganizationAuditEntry>>(cancellationToken);

    private static Task<UiCapabilityResult<T>> Unavailable<T>(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<T>("organization-management"));
    }
}
