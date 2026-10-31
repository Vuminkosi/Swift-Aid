using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Sessions;

/// <summary>Staff outcome of a session command issued from a Blazor Server page.</summary>
/// <param name="Succeeded">True when the service accepted the command.</param>
/// <param name="Result">Accepted command result; null when the command failed.</param>
/// <param name="Error">Plain-language failure description for the officer; null on success.</param>
/// <param name="VersionConflict">True when the session changed before the command was recorded.</param>
public sealed record SessionStaffOutcome(
    bool Succeeded,
    SessionCommandResult? Result,
    string? Error,
    bool VersionConflict);

/// <summary>
/// Server-side session client for Blazor Server. Public reads remain available;
/// staff reads and mutations fail closed until a trusted scoped-staff capability
/// contract is available. Authentication or role claims alone do not grant it.
/// </summary>
public sealed class SessionGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider)
{
    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <summary>Lists the published public sessions; empty when the pipeline is disabled.</summary>
    public async Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var service = _serviceProvider.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return [];
        }

        try
        {
            return await service.ListPublicAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Loads the plain-text invitation for a published session, if available.</summary>
    public async Task<string?> GetInvitationTextAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var service = _serviceProvider.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return null;
        }

        try
        {
            return await service.GetInvitationTextAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Loads the staff view used to prefill the editor; null when unavailable.</summary>
    public async Task<CommunitySessionView?> GetStaffAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!await HasTrustedScopedStaffCapabilityAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var actor = await GetActorAsync().ConfigureAwait(false);
        var service = _serviceProvider.GetService<ICommunitySessionService>();
        if (actor is null || service is null)
        {
            return null;
        }

        try
        {
            return await service.GetStaffAsync(sessionId, actor.ActorId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<string?> GetCurrentActorIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actor = await GetActorAsync().ConfigureAwait(false);
        return actor?.ActorId;
    }

    /// <summary>Executes an officer command against the known session version.</summary>
    public async Task<SessionStaffOutcome> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await HasTrustedScopedStaffCapabilityAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SessionStaffOutcome(
                false, null, "The community session was not found.", false);
        }

        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return new SessionStaffOutcome(
                false, null, "Sign in with a staff account to change community sessions.", false);
        }

        var service = _serviceProvider.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return new SessionStaffOutcome(
                false, null, "Community sessions are temporarily unavailable. Please try again later.", false);
        }

        try
        {
            var result = await service.ExecuteAsync(command, actor.ActorId, cancellationToken).ConfigureAwait(false);
            if (result.SessionId == Guid.Empty)
            {
                return new SessionStaffOutcome(
                    false, null, "Community sessions are temporarily unavailable. Please try again later.", false);
            }

            return new SessionStaffOutcome(true, result, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new SessionStaffOutcome(
                false, null, "The session details are invalid. Check the highlighted fields and try again.", false);
        }
        catch (InvalidOperationException exception) when (IsVersionConflict(exception))
        {
            return new SessionStaffOutcome(
                false, null, "The session changed before the change was recorded. Reload and try again.", true);
        }
        catch (InvalidOperationException)
        {
            return new SessionStaffOutcome(
                false, null, "The session cannot make that change in its current state.", false);
        }
        catch (UnauthorizedAccessException)
        {
            return new SessionStaffOutcome(
                false, null, "The community session was not found.", false);
        }
        catch (Exception)
        {
            return new SessionStaffOutcome(
                false, null, "Community sessions are temporarily unavailable. Please try again later.", false);
        }
    }

    private static bool IsVersionConflict(InvalidOperationException exception)
    {
        var message = exception.Message;
        return message.Contains("version", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("concurrency", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("stale", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("changed before", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ActorContext?> GetActorAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = state.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Task<bool> HasTrustedScopedStaffCapabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No trusted officer/current-scope capability contract is currently
        // wired. Do not infer staff authority from authentication or role claims.
        return Task.FromResult(false);
    }
}
