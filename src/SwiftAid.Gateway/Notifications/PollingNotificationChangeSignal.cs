using SwiftAid.Application.Authorization;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Gateway.Notifications;

/// <summary>Heartbeat-only signal for installations without Redis; clients refetch the authorized feed.</summary>
public sealed class PollingNotificationChangeSignal : INotificationChangeSignal
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public PollingNotificationChangeSignal(SwiftAidDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<NotificationChangeWaitResult> WaitForChangeAsync(
        ActorContext actor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (!await IsCurrentAsync(actor, cancellationToken).ConfigureAwait(false))
        {
            return NotificationChangeWaitResult.ActorNoLongerAuthorized;
        }

        await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
        return await IsCurrentAsync(actor, cancellationToken).ConfigureAwait(false)
            ? NotificationChangeWaitResult.Heartbeat
            : NotificationChangeWaitResult.ActorNoLongerAuthorized;
    }

    private async ValueTask<bool> IsCurrentAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        try
        {
            var state = await (
                    from currentActor in _dbContext.Actors.AsNoTracking()
                    join accessState in _dbContext.ActorAccessStates.AsNoTracking()
                        on currentActor.ActorId equals accessState.ActorId
                    where currentActor.ActorId == actor.ActorId
                    select new
                    {
                        currentActor.Status,
                        currentActor.ExpiresAtUtc,
                        accessState.AccessEpoch
                    })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return state is not null &&
                state.Status == PersistentActorStatus.Active &&
                state.AccessEpoch == actor.AccessEpoch &&
                (state.ExpiresAtUtc is null || state.ExpiresAtUtc > _timeProvider.GetUtcNow());
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
}
