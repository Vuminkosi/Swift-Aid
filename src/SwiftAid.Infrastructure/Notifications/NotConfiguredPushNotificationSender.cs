using SwiftAid.Application.Abstractions;

namespace SwiftAid.Infrastructure.Notifications;

public sealed class NotConfiguredPushNotificationSender : IPushNotificationSender
{
    public ValueTask<PushNotificationResult> SendAsync(
        PushNotificationRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new PushNotificationResult(PushNotificationStatus.Cancelled)
            : new PushNotificationResult(PushNotificationStatus.NotConfigured));
}
