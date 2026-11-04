using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Sessions;

/// <summary>Presentation adapter over the authenticated Gateway session editor.</summary>
public sealed class GatewayStaffSessionEditorClient(SessionGatewayClient gatewayClient, IOrganizationMembershipClient membershipClient) : IStaffSessionEditorClient
{
    private const string Capability = "community-session-editor";
    private readonly IOrganizationMembershipClient _membershipClient = membershipClient ?? throw new ArgumentNullException(nameof(membershipClient));

    public async Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default)
    {
        var actorId = await gatewayClient.GetCurrentActorIdAsync(cancellationToken).ConfigureAwait(false);
        return actorId is null
            ? Result<string>(UiCapabilityResultCode.Denied, null, "Sign in with an active organization owner account to manage sessions.", available: true)
            : Result(UiCapabilityResultCode.Success, actorId, null, available: true);
    }

    public async Task<UiCapabilityResult<StaffSessionManagementCapabilities>> GetManagementCapabilitiesAsync(
        Guid? sessionId,
        long? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var actorId = await gatewayClient.GetCurrentActorIdAsync(cancellationToken).ConfigureAwait(false);
        if (actorId is null)
        {
            return Result<StaffSessionManagementCapabilities>(
                UiCapabilityResultCode.Denied,
                null,
                "Sign in to check session management access.",
                available: true);
        }

        var membershipResult = await _membershipClient.GetMineAsync(cancellationToken).ConfigureAwait(false);
        if (!membershipResult.Succeeded || membershipResult.Value is null)
            return Result<StaffSessionManagementCapabilities>(UiCapabilityResultCode.Unavailable, null,
                "Organization ownership could not be confirmed.", available: false);

        var ownerOrganizations = membershipResult.Value.Where(item => item.IsOwner &&
                item.MembershipStatus == "Active" && item.OrganizationStatus == "Active")
            .Select(item => item.OrganizationId).Distinct(StringComparer.Ordinal).ToArray();
        if (sessionId is null)
        {
            var create = new StaffSessionManagementCapabilities(ownerOrganizations.Length > 0, false, false, false,
                false, false, null, null, null, true, OwnerOrganizationIds: ownerOrganizations);
            return Result(UiCapabilityResultCode.Success, create, null, available: true);
        }

        var session = await gatewayClient.GetStaffAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return Result<StaffSessionManagementCapabilities>(UiCapabilityResultCode.Denied, null,
                "The session is not available to this account.", available: true);
        var current = expectedVersion == session.Version;
        var canEdit = current && session.Status is "Draft" or "Reviewed" or "Scheduled";
        var caps = new StaffSessionManagementCapabilities(false, canEdit, current && session.Status == "Draft",
            current && session.Status == "Reviewed", current && session.Status is "Reviewed" or "Scheduled",
            current && session.Status == "Scheduled" && session.StartUtc <= DateTimeOffset.UtcNow,
            session.SessionId, session.Version, session.Status, current, CanViewHistory: true,
            OwnerOrganizationIds: ownerOrganizations);
        return Result(UiCapabilityResultCode.Success, caps, null, available: true);
    }

    public async Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Result<CommunitySessionView>(UiCapabilityResultCode.InvalidRequest, null, "Choose a valid session.", available: true);

        var session = await gatewayClient.GetStaffAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return session is null
            ? Result<CommunitySessionView>(UiCapabilityResultCode.Denied, null, "The session is not available to this account.", available: true)
            : Result(UiCapabilityResultCode.Success, session, null, available: true);
    }

    public async Task<UiCapabilityResult<SessionCommandResult>> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var outcome = await gatewayClient.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        var code = outcome.Succeeded
            ? UiCapabilityResultCode.Success
            : outcome.VersionConflict
                ? UiCapabilityResultCode.Conflict
                : outcome.Error?.Contains("sign in", StringComparison.OrdinalIgnoreCase) == true
                    ? UiCapabilityResultCode.Denied
                    : UiCapabilityResultCode.Unavailable;
        return Result(code, outcome.Result, outcome.Error, available: outcome.Succeeded || outcome.VersionConflict);
    }

    private static UiCapabilityResult<T> Result<T>(UiCapabilityResultCode code, T? value, string? message, bool available) =>
        new(code, value, new UiCapabilityMetadata(
            Capability,
            UiCapabilityResults.ContractVersion,
            UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow,
            available),
            message);
}
