namespace SwiftAid.Contracts.Cases;

public sealed record OfficerEscalationReviewItem(Guid EscalationId, DateTimeOffset RaisedAtUtc);
public sealed record OfficerEscalationReviewContext(long CaseVersion, IReadOnlyList<OfficerEscalationReviewItem> Escalations,
    bool MoreEscalations, IReadOnlyList<EligibleHealthOfficerCandidate> Officers, bool MoreOfficers);
public sealed record OfficerEscalationReviewedSelection(Guid CaseId, long CaseVersion, Guid EscalationId,
    string OfficerActorId, Guid DesignationId, long DesignationVersion, Guid FollowUpId,
    DateTimeOffset AcknowledgementDueUtc, DateTimeOffset FollowUpDueUtc, Guid ApprovalId, Guid ApprovalCommandId, Guid AssignmentCommandId);

public enum OfficerEscalationActionStatus { Recorded, Replayed, Denied, Conflict, Invalid, Unavailable, OutcomeUnknown }
public sealed record OfficerEscalationActionResult(OfficerEscalationActionStatus Status, Guid? EventId = null, long? CaseVersion = null);

/// <summary>Human-operated review and confirmation; the server independently authorizes each call.</summary>
public interface IOfficerEscalationReviewClient
{
    Task<OfficerEscalationReviewContext?> GetAsync(Guid caseId, CancellationToken ct = default);
    Task<OfficerEscalationActionResult> ApproveAsync(OfficerEscalationReviewedSelection selection, CancellationToken ct = default);
    Task<OfficerEscalationActionResult> AssignAsync(OfficerEscalationReviewedSelection selection, CancellationToken ct = default);
}
