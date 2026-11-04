using SwiftAid.Application.Abstractions;

namespace SwiftAid.Infrastructure.Messaging;

public sealed class NotConfiguredInAppNotificationPublisher : IInAppNotificationPublisher
{
    public ValueTask<InAppNotificationPublishResult> PublishAsync(
        InAppNotification notification,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new InAppNotificationPublishResult(InAppNotificationPublishStatus.Cancelled)
            : new InAppNotificationPublishResult(InAppNotificationPublishStatus.NotConfigured));
}
