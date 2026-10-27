using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Authorization;
using SwiftAid.Client.Notifications;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Gateway.Notifications;

/// <summary>
/// In-process notification client for the Gateway InteractiveServer host. It uses the current
/// circuit principal and never loops back through HTTP or forwards browser cookies.
/// </summary>
public sealed class InteractiveServerNotificationFeedClient : INotificationFeedClient
{
    private static readonly TimeSpan ChangeWaitInterval = TimeSpan.FromSeconds(20);
    private const int PageSize = 100;

    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly INotificationApiService _notifications;
    private readonly INotificationChangeSignal _changes;

    public InteractiveServerNotificationFeedClient(
        AuthenticationStateProvider authenticationStateProvider,
        INotificationApiService notifications,
        INotificationChangeSignal changes)
    {
        _authenticationStateProvider = authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    public async Task<NotificationFeedResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return NotificationFeedResult.PermissionDenied();
        }

        try
        {
            var result = await _notifications.GetAsync(actor, PageSize, null, cancellationToken)
                .ConfigureAwait(false);
            return NotificationFeedResult.Ready(result.Items);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NotificationFeedResult.Unavailable();
        }
    }

    public async Task<bool> MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return false;
        }

        try
        {
            return await _notifications.MarkReadAsync(actor, notificationId, cancellationToken)
                .ConfigureAwait(false) == NotificationMutationStatus.Applied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    public async Task<bool> DismissAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return false;
        }

        try
        {
            return await _notifications.DismissAsync(actor, notificationId, cancellationToken)
                .ConfigureAwait(false) == NotificationMutationStatus.Applied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    public async Task<PushDeviceRegistrationResult> RegisterDeviceAsync(
        Guid installationId,
        PushDevicePlatform platform,
        string token,
        bool soundEnabled,
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return new PushDeviceRegistrationResult(PushDeviceRegistrationStatus.NotAuthenticated);
        }

        try
        {
            var status = await _notifications.RegisterDeviceAsync(
                actor,
                new PushDeviceRegistrationRequest(installationId, platform, token, soundEnabled),
                cancellationToken).ConfigureAwait(false);
            return new PushDeviceRegistrationResult(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new PushDeviceRegistrationResult(PushDeviceRegistrationStatus.Unavailable);
        }
    }

    public async Task<PushDeviceRemovalResult> RemoveDeviceAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return new PushDeviceRemovalResult(PushDeviceRemovalStatus.NotAuthenticated);
        }

        try
        {
            var status = await _notifications.RemoveDeviceAsync(actor, installationId, cancellationToken)
                .ConfigureAwait(false);
            return new PushDeviceRemovalResult(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new PushDeviceRemovalResult(PushDeviceRemovalStatus.Unavailable);
        }
    }

    public async IAsyncEnumerable<bool> WatchChangesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ActorContext? actor;
            try
            {
                actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (actor is null)
            {
                yield break;
            }

            NotificationChangeWaitResult result;
            try
            {
                result = await _changes.WaitForChangeAsync(actor, ChangeWaitInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                yield break;
            }

            if (result == NotificationChangeWaitResult.ActorNoLongerAuthorized)
            {
                yield break;
            }

            // Both change and heartbeat are opaque hints; callers refetch through GetAsync.
            yield return result == NotificationChangeWaitResult.Changed;
        }
    }

    public async Task MonitorChangesAsync(
        Func<CancellationToken, Task> refreshAuthorizedFeedAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshAuthorizedFeedAsync);

        while (!cancellationToken.IsCancellationRequested)
        {
            var ended = false;
            try
            {
                await foreach (var _ in WatchChangesAsync(cancellationToken).ConfigureAwait(false))
                {
                    await refreshAuthorizedFeedAsync(cancellationToken).ConfigureAwait(false);
                }

                ended = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Continue with bounded feed polling after the server-side signal is unavailable.
            }

            if (ended || !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                await refreshAuthorizedFeedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<ActorContext?> GetActorAsync(CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return ActorContext.FromValidatedPrincipal(state.User);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

}
