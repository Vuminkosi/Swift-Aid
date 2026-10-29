namespace SwiftAid.Client.Presentation;

public sealed record MembershipView(
    string OrganizationId,
    string OrganizationStatus,
    string MembershipStatus,
    IReadOnlyList<string> WorkspaceIds);

public sealed record OrganizationDirectoryOption(string OrganizationId, string Status);

public sealed record MembershipRequestView(
    string RequestId,
    string OrganizationId,
    string Status,
    string? Reason);

public sealed record InvitationRedeemView(string OrganizationId, string Status);

/// <summary>Member-side organization access: my memberships, directory, join requests.</summary>
public interface IOrganizationMembershipClient
{
    Task<UiCapabilityResult<IReadOnlyList<MembershipView>>> GetMineAsync(
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<IReadOnlyList<OrganizationDirectoryOption>>> SearchAsync(
        string? query,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<MembershipRequestView>> RequestAsync(
        string organizationId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<IReadOnlyList<MembershipRequestView>>> GetMyRequestsAsync(
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<InvitationRedeemView>> RedeemAsync(
        string code,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableOrganizationMembershipClient : IOrganizationMembershipClient
{
    public Task<UiCapabilityResult<IReadOnlyList<MembershipView>>> GetMineAsync(
        CancellationToken cancellationToken = default) =>
        Unavailable<IReadOnlyList<MembershipView>>(cancellationToken);

    public Task<UiCapabilityResult<IReadOnlyList<OrganizationDirectoryOption>>> SearchAsync(
        string? query,
        CancellationToken cancellationToken = default) =>
        Unavailable<IReadOnlyList<OrganizationDirectoryOption>>(cancellationToken);

    public Task<UiCapabilityResult<MembershipRequestView>> RequestAsync(
        string organizationId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        return Unavailable<MembershipRequestView>(cancellationToken);
    }

    public Task<UiCapabilityResult<IReadOnlyList<MembershipRequestView>>> GetMyRequestsAsync(
        CancellationToken cancellationToken = default) =>
        Unavailable<IReadOnlyList<MembershipRequestView>>(cancellationToken);

    public Task<UiCapabilityResult<InvitationRedeemView>> RedeemAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Unavailable<InvitationRedeemView>(cancellationToken);
    }

    private static Task<UiCapabilityResult<T>> Unavailable<T>(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<T>("organization-membership"));
    }
}
