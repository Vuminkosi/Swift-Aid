namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>Durable recipient/channel work derived from a scheduled session reminder.</summary>
public sealed class SessionReminderDeliveryEntity
{
    public Guid DeliveryId { get; set; }
    public Guid OccurrenceId { get; set; }
    public Guid SessionId { get; set; }
    public required string AccountId { get; set; }
    public required string Channel { get; set; }
    public long SessionVersion { get; set; }
    public long SubscriptionVersion { get; set; }
    public required string ConsentReference { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
