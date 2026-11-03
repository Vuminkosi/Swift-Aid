using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Gateway.Sessions;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Server-side public session discovery for Blazor Server: delegates to the circuit-scoped session gateway client.</summary>
public sealed class WebPublicCommunitySessionClient(SessionGatewayClient sessions) : IPublicCommunitySessionClient
{
    private readonly SessionGatewayClient _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public async Task<UiCapabilityResult<PublicSessionView>> GetPublicAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return new UiCapabilityResult<PublicSessionView>(UiCapabilityResultCode.InvalidRequest, null,
                UiCapabilityResults.UnavailableMetadata("public-community-sessions", "Choose a valid session."), "Choose a valid session.");
        try
        {
            var session = await _sessions.GetPublicAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return new UiCapabilityResult<PublicSessionView>(UiCapabilityResultCode.Success, session, LiveMetadata("public-community-sessions"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<PublicSessionView>("public-community-sessions");
        }
    }

    public async Task<UiCapabilityResult<PublicSessionPage>> QueryPublicAsync(
        PublicSessionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var page = await _sessions.QueryPublicAsync(query, cancellationToken).ConfigureAwait(false);
            return new UiCapabilityResult<PublicSessionPage>(UiCapabilityResultCode.Success, page,
                LiveMetadata("public-community-sessions"));
        }
        catch (ArgumentException)
        {
            return new UiCapabilityResult<PublicSessionPage>(UiCapabilityResultCode.InvalidRequest, null,
                UiCapabilityResults.UnavailableMetadata("public-community-sessions", "The session search is invalid."),
                "The session search is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<PublicSessionPage>("public-community-sessions");
        }
    }

    /// <summary>Lists public sessions using the service's supported limit range of 1 through 100.</summary>
    public async Task<UiCapabilityResult<IReadOnlyList<PublicSessionView>>> ListPublicAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100)
        {
            return new UiCapabilityResult<IReadOnlyList<PublicSessionView>>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                UiCapabilityResults.UnavailableMetadata(
                    "public-community-sessions", "Choose a session limit between 1 and 100."),
                "Choose a session limit between 1 and 100.");
        }

        try
        {
            var sessions = await _sessions.ListPublicAsync(limit, cancellationToken).ConfigureAwait(false);
            return new UiCapabilityResult<IReadOnlyList<PublicSessionView>>(
                UiCapabilityResultCode.Success,
                sessions,
                LiveMetadata("public-community-sessions"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<IReadOnlyList<PublicSessionView>>("public-community-sessions");
        }
    }

    public async Task<UiCapabilityResult<string>> GetInvitationTextAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty)
        {
            return new UiCapabilityResult<string>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                UiCapabilityResults.UnavailableMetadata(
                    "public-community-sessions", "Choose a valid session."),
                "Choose a valid session.");
        }

        try
        {
            var invitation = await _sessions.GetInvitationTextAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return invitation is null
                ? UiCapabilityResults.Unavailable<string>("public-community-sessions")
                : new UiCapabilityResult<string>(
                    UiCapabilityResultCode.Success,
                    invitation,
                    LiveMetadata("public-community-sessions"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<string>("public-community-sessions");
        }
    }

    private static UiCapabilityMetadata LiveMetadata(string capability) => new(
        capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
