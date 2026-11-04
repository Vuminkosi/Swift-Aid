using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Client.Notifications;

public interface INotificationFeedClient
{
    Task<NotificationFeedResult> GetAsync(CancellationToken cancellationToken = default);

    Task<bool> MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default);

    Task<bool> DismissAsync(Guid notificationId, CancellationToken cancellationToken = default);

    Task<PushDeviceRegistrationResult> RegisterDeviceAsync(
        Guid installationId,
        PushDevicePlatform platform,
        string token,
        bool soundEnabled,
        CancellationToken cancellationToken = default);

    Task<PushDeviceRemovalResult> RemoveDeviceAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<bool> WatchChangesAsync(CancellationToken cancellationToken = default);

    Task MonitorChangesAsync(
        Func<CancellationToken, Task> refreshAuthorizedFeedAsync,
        CancellationToken cancellationToken = default);
}
