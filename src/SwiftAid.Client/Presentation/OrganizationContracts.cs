namespace SwiftAid.Client.Presentation;

public sealed record OrganizationWorkspaceQuery(
    PageRequest Page,
    string? Search = null,
    string? Region = null,
    string? Role = null);

public sealed record OrganizationMember(
    string Id,
    string DisplayName,
    string Role,
    string Status,
    bool IsSynthetic);

public sealed record OrganizationInvitation(
    string Id,
    string Email,
    string Role,
    string Status,
    bool IsSynthetic);

public sealed record OrganizationWorkspace(
    Page<OrganizationMember> Members,
    Page<OrganizationInvitation> Invitations,
    bool CanManageMembers);

public sealed record InvitationPreviewRequest(string Email, string Role);

public interface IOrganizationClient
{
    Task<UiCapabilityResult<OrganizationWorkspace>> GetWorkspaceAsync(
        OrganizationWorkspaceQuery query,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationInvitation>> PreviewInvitationAsync(
        InvitationPreviewRequest request,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<OrganizationInvitation>> CancelInvitationPreviewAsync(
        string invitationId,
        CancellationToken cancellationToken = default);
}
