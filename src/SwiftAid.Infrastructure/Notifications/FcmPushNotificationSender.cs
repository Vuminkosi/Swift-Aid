using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Abstractions;

namespace SwiftAid.Infrastructure.Notifications;

public sealed class FcmPushNotificationSender : IPushNotificationSender
{
    private readonly FirebaseMessaging _messaging;
    private readonly ILogger<FcmPushNotificationSender> _logger;

    public FcmPushNotificationSender(
        FirebaseMessaging messaging,
        ILogger<FcmPushNotificationSender> logger)
    {
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<PushNotificationResult> SendAsync(
        PushNotificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return new PushNotificationResult(PushNotificationStatus.Cancelled);
        }

        if (string.IsNullOrWhiteSpace(request.DeviceToken) ||
            request.NotificationId == Guid.Empty ||
            !Enum.IsDefined(request.Kind))
        {
            return new PushNotificationResult(
                PushNotificationStatus.Failed,
                FailureCode: "invalid-notification");
        }

        var message = new Message
        {
#pragma warning disable CS0618 // The approved port carries an FCM registration token, not a Firebase Installation ID.
            Token = request.DeviceToken,
#pragma warning restore CS0618
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = request.Kind.ToString(),
                ["notificationId"] = request.NotificationId.ToString("N")
            },
            Android = request.Platform == SwiftAid.Contracts.Notifications.PushDevicePlatform.Android
                ? new AndroidConfig
                {
                    Priority = Priority.Normal,
                    Notification = new AndroidNotification
                    {
                        Title = "SwiftAid update",
                        Body = "Open SwiftAid to view an update.",
                        ChannelId = request.SoundEnabled
                            ? "swiftaid_notifications_gentle_v1"
                            : "swiftaid_notifications_quiet_v1",
                        Sound = request.SoundEnabled ? "relaxing_synth_ping" : null
                    }
                }
                : null,
            Apns = request.Platform == SwiftAid.Contracts.Notifications.PushDevicePlatform.Ios
                ? new ApnsConfig
                {
                    Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["apns-push-type"] = "alert",
                        ["apns-priority"] = "10"
                    },
                    Aps = new Aps
                    {
                        Alert = new ApsAlert
                        {
                            Title = "SwiftAid update",
                            Body = "Open SwiftAid to view an update."
                        },
                        Sound = request.SoundEnabled ? "swiftaid_gentle_notification.wav" : null,
                        ContentAvailable = true
                    }
                }
                : null
        };

        try
        {
            var providerMessageId = await _messaging.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return new PushNotificationResult(
                PushNotificationStatus.AcceptedByProvider,
                providerMessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PushNotificationResult(PushNotificationStatus.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("FCM push submission failed. Failure type: {FailureType}", exception.GetType().Name);
            return new PushNotificationResult(
                PushNotificationStatus.Failed,
                FailureCode: "push-submission-failure");
        }
    }
}
