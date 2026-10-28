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
    /// <summary>Coarse routing-assessment country carried in cleartext; sealed details stay protected.</summary>
    public string? RoutingCountryCode { get; set; }
    public string? RoutingRegionId { get; set; }
    public string? RoutingAreaId { get; set; }
    public string? RoutingCatalogueVersion { get; set; }
    public IntakeSubjectScope? SubjectScope { get; set; }
    /// <summary>Coordinator that claimed administrative review (self-claim only); never a clinical owner.</summary>
    public string? CoordinatorActorId { get; set; }
    public DateTimeOffset? CoordinatorAssignedAtUtc { get; set; }
    public DateTimeOffset? ClarificationRequestedAtUtc { get; set; }
    public string? ClarificationReason { get; set; }
}
