using SwiftAid.Domain.Cases;

namespace SwiftAid.Infrastructure.Persistence.Entities.Cases;

/// <summary>Monitoring read-model row for one scheduled case follow-up and its latest outcome status.</summary>
public sealed class ManagedCaseFollowUpEntity
{
    public Guid FollowUpId { get; set; }
    public Guid CaseId { get; set; }
    public required string Purpose { get; set; }
    public required string AssigneeActorId { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public FollowUpStatus Status { get; set; }
    public long ScheduledVersion { get; set; }
    public long CurrentScheduleVersion { get; set; }
    public Guid CurrentScheduleEventId { get; set; }
    // Null means legacy/unknown; 0 means ownership changed; 1 means not yet
    // acknowledged. Real acknowledgment events have an aggregate version above 1.
    public long? AcknowledgedVersion { get; set; }
    public long? OutcomeVersion { get; set; }
}
