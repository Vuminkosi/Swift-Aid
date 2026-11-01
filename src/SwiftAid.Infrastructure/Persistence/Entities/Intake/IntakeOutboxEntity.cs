namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

/// <summary>PII-free durable notification of a committed intake transition.</summary>
public sealed class IntakeOutboxEntity
{
    public Guid OutboxId { get; set; }
    public Guid ReceiptId { get; set; }
    public long Revision { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset? ExhaustedAtUtc { get; set; }
    public string? LastFailureCode { get; set; }
}
