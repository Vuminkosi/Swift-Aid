namespace SwiftAid.Client.Presentation;

/// <summary>Fail-closed workspace and invitation capability until an authorized organization service exists.</summary>
public sealed class UnavailableOrganizationClient : IOrganizationClient
{
    public Task<UiCapabilityResult<OrganizationWorkspace>> GetWorkspaceAsync(
        OrganizationWorkspaceQuery query,
        CancellationToken cancellationToken = default) =>
        Unavailable<OrganizationWorkspace>(cancellationToken);

    public Task<UiCapabilityResult<OrganizationInvitation>> PreviewInvitationAsync(
        InvitationPreviewRequest request,
        CancellationToken cancellationToken = default) =>
        Unavailable<OrganizationInvitation>(cancellationToken);

    public Task<UiCapabilityResult<OrganizationInvitation>> CancelInvitationPreviewAsync(
        string invitationId,
        CancellationToken cancellationToken = default) =>
        Unavailable<OrganizationInvitation>(cancellationToken);

    private static Task<UiCapabilityResult<T>> Unavailable<T>(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<T>("organization-management"));
    }
}
