namespace SwiftAid.Infrastructure.Persistence.Entities.Cases;

/// <summary>Monitoring read-model row for one staff-recorded case observation.</summary>
public sealed class ManagedCaseObservationEntity
{
    public Guid ObservationId { get; set; }
    public Guid CaseId { get; set; }
    public required string Narrative { get; set; }
    public required string Source { get; set; }
    public long AggregateVersion { get; set; }
}
