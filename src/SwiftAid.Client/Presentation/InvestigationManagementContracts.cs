namespace SwiftAid.Client.Presentation;

public sealed record InvestigationWorkItem(
    string Id,
    string Title,
    string Objective,
    InvestigationStatus Status,
    long Version,
    DateTimeOffset UpdatedAtUtc,
    bool IsSynthetic);

public sealed record InvestigationProposalReview(
    string InvestigationId,
    string ProposalId,
    bool Approve,
    string Reason,
    long ExpectedVersion);

public sealed record InvestigationExecutionCheck(
    string Name,
    bool Passed,
    string Summary);

public sealed record InvestigationExecutionAndVerification(
    string InvestigationId,
    long Version,
    InvestigationExecutionStatus ExecutionStatus,
    InvestigationVerificationStatus VerificationStatus,
    string? ResultSummary,
    IReadOnlyList<InvestigationExecutionCheck> Checks,
    DateTimeOffset UpdatedAtUtc,
    bool IsSynthetic,
    string? Notice);
