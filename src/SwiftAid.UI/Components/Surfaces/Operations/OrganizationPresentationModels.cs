namespace SwiftAid.UI.Components.Surfaces.Operations;

public enum OrganizationRole { Member, Coordinator, Officer, Administrator }
public enum OrganizationCountry { Zimbabwe, Eswatini, Other }
public enum OrganizationRegion
{
    Harare, Bulawayo, Manicaland, MashonalandCentral, MashonalandEast, MashonalandWest,
    Masvingo, MatabelelandNorth, MatabelelandSouth, Midlands, Hhohho, Lubombo, Manzini,
    Shiselweni, Other
}
public enum OrganizationMembershipRequestStatus { Pending, Approved, Declined }
public enum OrganizationSsoStatus { NotConfigured, SetupPending, VerificationRequired, Unavailable }
public enum OrganizationAuditAction
{
    ProfileUpdated, MembershipRequested, MembershipApproved, MembershipDeclined,
    InvitationPreviewCreated, InvitationPreviewCancelled, MemberRoleChanged,
    MemberRegionChanged, MembershipRevoked, SsoConfigurationChanged
}

public sealed record OrganizationProfile(string DisplayName, OrganizationCountry Country, OrganizationRegion Region);
public sealed record OrganizationProfileUpdate(string DisplayName, OrganizationCountry Country, OrganizationRegion Region, string Reason);
public sealed record OrganizationMembershipRequest(
    string Id, string RequesterName, OrganizationCountry Country, OrganizationRegion Region,
    OrganizationRole RequestedRole, OrganizationMembershipRequestStatus Status, DateTimeOffset CreatedAtUtc);
public sealed record OrganizationMembershipRequestSubmission(
    string RequesterName, OrganizationCountry Country, OrganizationRegion Region, OrganizationRole RequestedRole, string Reason);
public sealed record OrganizationInvitationRequest(
    string Email, OrganizationRole Role, OrganizationCountry Country, OrganizationRegion Region, string Reason);
public sealed record OrganizationMembershipRequestReview(string RequestId, bool Approve, string Reason);
public sealed record OrganizationInvitation(string Id, string Email, string Role, string Status);
public sealed record ManagedOrganizationMember(
    string Id, string DisplayName, OrganizationRole Role, OrganizationCountry Country, OrganizationRegion Region, string Status);
public sealed record OrganizationMemberChange(
    string MemberId, OrganizationRole? Role, OrganizationCountry? Country, OrganizationRegion? Region, string Reason);
public sealed record OrganizationMembershipRevocation(string MemberId, string Reason);
public sealed record OrganizationSsoConfiguration(OrganizationSsoStatus Status, string Description, bool HasStoredSecret);
public sealed record OrganizationAuditEntry(OrganizationAuditAction Action, string Summary, DateTimeOffset OccurredAtUtc);
