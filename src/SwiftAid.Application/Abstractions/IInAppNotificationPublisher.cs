namespace SwiftAid.Application.Abstractions;

public enum InAppNotificationKind
{
    CaseUpdateAvailable
}

/// <summary>
/// Minimal, non-clinical notification event. Consumers must re-authorize access before returning
/// any case or report details; this message is not an authorization grant.
/// </summary>
public sealed record InAppNotification(
    Guid NotificationId,
    string RecipientActorId,
    InAppNotificationKind Kind,
    Guid RelatedEventId,
    DateTimeOffset CreatedAtUtc);

public enum InAppNotificationPublishStatus
{
    AcknowledgedByJetStream,
    NotConfigured,
    Unavailable,
    Failed,
    Cancelled
}

public sealed record InAppNotificationPublishResult(
    InAppNotificationPublishStatus Status,
    string? FailureCode = null);

/// <summary>Publishes in-app notifications to a durable server-side JetStream stream.</summary>
public interface IInAppNotificationPublisher
{
    ValueTask<InAppNotificationPublishResult> PublishAsync(
        InAppNotification notification,
        CancellationToken cancellationToken);
}
