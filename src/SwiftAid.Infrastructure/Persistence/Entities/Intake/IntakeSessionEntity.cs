namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

/// <summary>Session-scoped receipt proof. Only the SHA-256 proof digest is persisted.</summary>
public sealed class IntakeSessionEntity
{
    public Guid SessionId { get; set; }
    public required byte[] ReceiptProofDigest { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
}
