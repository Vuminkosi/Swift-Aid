namespace SwiftAid.Contracts.Commands;

/// <summary>Closed set of protected actions that may be requested for human review.</summary>
public sealed record CaseActionRequestPayload(
    Guid RequestId,
    CaseAction RequestedOperation,
    string Reason,
    string? ProposedOwnerId,
    IReadOnlyList<string> EvidenceEventIds);

public enum CaseActionReviewOutcome
{
    Pending,
    Denied,
    Approved
}
