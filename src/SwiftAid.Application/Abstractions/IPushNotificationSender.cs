using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Application.Abstractions;

public enum PushNotificationKind
{
    CaseUpdateAvailable
}

/// <summary>
/// Contains only a device token and a non-clinical event identifier. The token must never be logged,
/// persisted in event payloads, or included in application/client configuration.
/// </summary>
public sealed record PushNotificationRequest(
    string DeviceToken,
    PushNotificationKind Kind,
    Guid NotificationId,
    PushDevicePlatform Platform = PushDevicePlatform.Web,
    bool SoundEnabled = false);

public enum PushNotificationStatus
{
    AcceptedByProvider,
    NotConfigured,
    Failed,
    Cancelled
}

public sealed record PushNotificationResult(
    PushNotificationStatus Status,
    string? ProviderMessageId = null,
    string? FailureCode = null);

/// <summary>
/// Sends only generic push notices. Provider acceptance does not prove device delivery or user receipt.
/// </summary>
public interface IPushNotificationSender
{
    ValueTask<PushNotificationResult> SendAsync(
        PushNotificationRequest request,
        CancellationToken cancellationToken);
}
