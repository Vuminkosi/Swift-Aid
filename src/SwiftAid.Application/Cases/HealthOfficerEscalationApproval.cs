using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Cases;

/// <summary>The review binds all assignment parameters; recording it does not assign or notify an officer.</summary>
public sealed record HealthOfficerEscalationApprovalRequest(Guid ApprovalId, Guid CommandId, Guid CaseId, Guid EscalationId,
    long ExpectedCaseVersion, string OfficerActorId, Guid DesignationId, long DesignationVersion, Guid FollowUpId,
    DateTimeOffset AcknowledgementDueUtc, DateTimeOffset FollowUpDueUtc);

public enum HealthOfficerEscalationApprovalStatus { Recorded, Replayed, Conflict, Denied, Invalid, Unavailable }
public sealed record HealthOfficerEscalationApprovalResult(HealthOfficerEscalationApprovalStatus Status, Guid? ApprovalId = null);

public interface IHealthOfficerEscalationApprovalService
{
    Task<HealthOfficerEscalationApprovalResult> ApproveAsync(ActorContext actor, HealthOfficerEscalationApprovalRequest request, CancellationToken ct);
}
