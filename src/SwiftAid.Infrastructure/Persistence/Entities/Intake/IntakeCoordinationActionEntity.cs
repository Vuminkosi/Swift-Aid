using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

/// <summary>PC-09 audit row: every intake coordination action with actor, authority, reason, time, and resulting state.</summary>
public sealed class IntakeCoordinationActionEntity
{
    public Guid ActionId { get; set; }
    public Guid ReceiptId { get; set; }
    public IntakeCoordinationAction Action { get; set; }
    public required string ActorId { get; set; }
    public required string CoordinationOrganizationId { get; set; }
    public required string GrantGroupId { get; set; }
    public string? Reason { get; set; }
    public IntakeStatus FromStatus { get; set; }
    public IntakeRoutingStatus FromRouting { get; set; }
    public string? FromQueueId { get; set; }
    public IntakeStatus ToStatus { get; set; }
    public IntakeRoutingStatus ToRouting { get; set; }
    public string? ToQueueId { get; set; }
    public long ResultingVersion { get; set; }
    public DateTimeOffset ActedAtUtc { get; set; }
}
