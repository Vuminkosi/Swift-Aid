namespace SwiftAid.Client.Notifications;

public enum NotificationPermissionState
{
    Unknown,
    NotSupported,
    Denied,
    Granted
}

public enum NotificationPlatformResult
{
    Enabled,
    Disabled,
    PermissionDenied,
    NotSupported,
    NotAuthenticated,
    Unavailable
}

public sealed record NotificationPlatformSettingsSnapshot(
    NotificationPermissionState Permission,
    bool PushEnabled,
    bool GentleSoundEnabled);

/// <summary>
/// User-controlled platform notification settings. Implementations request operating-system
/// permission only from EnableAsync, which must be called directly from a user action.
/// </summary>
public interface INotificationPlatformSettings
{
    Task<NotificationPlatformSettingsSnapshot> GetStateAsync(
        CancellationToken cancellationToken = default);

    Task<NotificationPermissionState> GetPermissionStatusAsync(
        CancellationToken cancellationToken = default);

    Task<NotificationPlatformResult> EnableAsync(
        bool playGentleSound,
        CancellationToken cancellationToken = default);

    Task<NotificationPlatformResult> SetGentleSoundAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<bool> TryPlayForegroundSoundAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default);

    Task<NotificationPlatformResult> DisableAsync(
        CancellationToken cancellationToken = default);
}
