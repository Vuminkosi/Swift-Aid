using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Cases;

public sealed record HealthOfficerEscalationAssignmentRequest(Guid CaseId, Guid ApprovalId, Guid CommandId);
public enum HealthOfficerEscalationAssignmentStatus { Recorded, Replayed, Conflict, Denied, Invalid, Unavailable }
public sealed record HealthOfficerEscalationAssignmentResult(HealthOfficerEscalationAssignmentStatus Status, Guid? FollowUpEventId = null, long? CaseVersion = null);

/// <summary>Executes only an existing exact approval; the case owner is not changed.</summary>
public interface IHealthOfficerEscalationAssignmentService
{
    Task<HealthOfficerEscalationAssignmentResult> AssignAsync(ActorContext actor, HealthOfficerEscalationAssignmentRequest request, CancellationToken ct);
}
