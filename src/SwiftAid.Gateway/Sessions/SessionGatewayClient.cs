using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Gateway.Endpoints;

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
/// staff reads and mutations are authorized against persisted organization ownership.
/// Authentication or role claims alone do not grant access.
/// </summary>
public sealed class SessionGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider)
{
    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <summary>Lists published public sessions; failures propagate to the presentation adapter.</summary>
    public async Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var service = _serviceProvider.GetService<ICommunitySessionService>()
            ?? throw new InvalidOperationException("The community session service is unavailable.");
        return await service.ListPublicAsync(limit, cancellationToken).ConfigureAwait(false);
    }

    public Task<PublicSessionPage> QueryPublicAsync(PublicSessionQuery query, CancellationToken cancellationToken = default)
    {
        var service = _serviceProvider.GetService<ICommunitySessionService>()
            ?? throw new InvalidOperationException("The community session service is unavailable.");
        return service.QueryPublicAsync(query, cancellationToken);
    }

    public Task<PublicSessionView?> GetPublicAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var service = _serviceProvider.GetService<ICommunitySessionService>()
            ?? throw new InvalidOperationException("The community session service is unavailable.");
        return service.GetPublicAsync(sessionId, cancellationToken);
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

    public async Task<string?> GetInvitationTextForVersionAsync(Guid sessionId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var service = _serviceProvider.GetService<ICommunitySessionService>();
        if (service is null || expectedVersion < 1) return null;
        try
        {
            return await service.GetInvitationTextForVersionAsync(sessionId, expectedVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
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
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        return actor?.ActorId;
    }

    /// <summary>Executes an officer command against the known session version.</summary>
    public async Task<SessionStaffOutcome> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return new SessionStaffOutcome(
                false, null, "Sign in with an active organization owner account to change community sessions.", false);
        }

        var service = _serviceProvider.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return new SessionStaffOutcome(
                false, null, "Community sessions are temporarily unavailable. Please try again later.", false);
        }

        try
        {
            // The editor can spend time checking access after creating a command.
            // Stamp it again at the trusted server boundary so that lifecycle and
            // reminder decisions do not use that earlier time.
            var acceptedCommand = command with { RecordedAtUtc = DateTimeOffset.UtcNow };
            var result = await service.ExecuteAsync(acceptedCommand, actor.ActorId, cancellationToken).ConfigureAwait(false);
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

    private async Task<ActorContext?> GetActorAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var principal = (await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            if (principal?.Identity?.IsAuthenticated != true) return null;
            var actor = ActorContext.FromValidatedPrincipal(principal);
            var access = _serviceProvider.GetService<IWorkspaceAccessRequest>();
            if (access is null) return null;
            var current = await access.GetAsync(principal, null, cancellationToken).ConfigureAwait(false);
            return current.Succeeded && current.Metadata.IsAvailable && current.Value is not null ? actor : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

}
