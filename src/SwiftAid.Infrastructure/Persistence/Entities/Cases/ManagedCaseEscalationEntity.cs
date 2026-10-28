using SwiftAid.Domain.Cases;

namespace SwiftAid.Infrastructure.Persistence.Entities.Cases;

/// <summary>Monitoring read-model row for one staff-owned case escalation and its latest status.</summary>
public sealed class ManagedCaseEscalationEntity
{
    public Guid EscalationId { get; set; }
    public Guid CaseId { get; set; }
    public EscalationStatus Status { get; set; }
    public long RaisedVersion { get; set; }
    public long? AcknowledgedVersion { get; set; }
    public long? ClosedVersion { get; set; }
}
