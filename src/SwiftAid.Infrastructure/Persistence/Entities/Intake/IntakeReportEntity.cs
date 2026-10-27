using SwiftAid.Contracts.Intake;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

/// <summary>Immutable submitted facts plus mutable review projection; source payload is protected at rest.</summary>
public sealed class IntakeReportEntity
{
    public Guid ReceiptId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ClientSubmissionId { get; set; }
    public required byte[] CanonicalPayloadDigest { get; set; }
    public required byte[] ProtectedPayload { get; set; }
    public IntakeStatus Status { get; set; }
    public IntakeRoutingStatus RoutingStatus { get; set; }
    public string? IntakeQueueId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public string? DecisionActorId { get; set; }
    public string? ReviewReason { get; set; }
}
