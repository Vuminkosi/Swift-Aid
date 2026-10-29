using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Sessions;

/// <summary>Presentation adapter over the authenticated Gateway session editor.</summary>
public sealed class GatewayStaffSessionEditorClient(SessionGatewayClient gatewayClient) : IStaffSessionEditorClient
{
    private const string Capability = "community-session-editor";

    public async Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default)
    {
        var actorId = await gatewayClient.GetCurrentActorIdAsync(cancellationToken).ConfigureAwait(false);
        return actorId is null
            ? Result<string>(UiCapabilityResultCode.Denied, null, "Sign in with an authorized staff account to manage sessions.", available: true)
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

        // The current Gateway session service exposes no authoritative per-action authorization query.
        // Authentication alone does not establish permission to create or manage sessions.
        return new UiCapabilityResult<StaffSessionManagementCapabilities>(
            UiCapabilityResultCode.Unavailable,
            null,
            new UiCapabilityMetadata(
                Capability,
                UiCapabilityResults.ContractVersion,
                UiCapabilityProvenance.LiveService,
                DateTimeOffset.UtcNow,
                IsAvailable: false,
                "Session management access could not be confirmed."),
            "Session management access could not be confirmed.");
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
