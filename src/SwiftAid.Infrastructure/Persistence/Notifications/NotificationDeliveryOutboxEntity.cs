namespace SwiftAid.Infrastructure.Persistence.Notifications;

/// <summary>Durable, bounded notification delivery work; contains no token or display content.</summary>
public sealed class NotificationDeliveryOutboxEntity
{
    public Guid OutboxId { get; set; }
    public Guid NotificationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public DateTimeOffset? ExhaustedAtUtc { get; set; }
}
