using SwiftAid.Application.Authorization;

namespace SwiftAid.Gateway.Notifications;

/// <summary>
/// Waits for an actor-scoped change signal. Implementations must recheck that the actor remains
/// current and authorized. The signal must not contain notification, actor, or resource details.
/// </summary>
public interface INotificationChangeSignal
{
    ValueTask<NotificationChangeWaitResult> WaitForChangeAsync(
        ActorContext actor,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public enum NotificationChangeWaitResult
{
    Changed,
    Heartbeat,
    ActorNoLongerAuthorized
}
