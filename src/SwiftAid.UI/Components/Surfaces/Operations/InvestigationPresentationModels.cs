namespace SwiftAid.UI.Components.Surfaces.Operations;

public enum OperationsInvestigationStatus
{
    NotStarted, GatheringEvidence, NeedsInput, Paused, AwaitingApproval, Resuming, Verifying,
    Partial, Stale, BudgetExceeded, Completed, Unavailable, Failed
}
public enum OperationsInvestigationExecutionStatus { NotStarted, AwaitingReview, InProgress, Partial, Completed, Failed, Stale, BudgetExceeded }
public enum OperationsInvestigationVerificationStatus { NotStarted, InProgress, Passed, Failed, Incomplete }
public sealed record OperationsInvestigationRequest(string Title, string Objective);
public sealed record OperationsInvestigationCommand(string InvestigationId, long ExpectedVersion);
public sealed record OperationsInvestigationClarificationSubmission(
    string InvestigationId, long ExpectedVersion, string ClarificationId, string Answer);
public sealed record OperationsInvestigationProposalRequest(
    string InvestigationId, long ExpectedVersion, string Summary);
public sealed record OperationsInvestigationEvidenceReference(string Id, string Label, string SourceVersion);
public sealed record OperationsInvestigationClarification(string Id, string FactualQuestion, string? Answer = null);
public sealed record OperationsInvestigationProposal(
    string Id, string Summary, bool RequiresHumanApproval, bool Executed, string? ReviewStatus = null, string? ReviewReason = null);
public sealed record OperationsInvestigationSnapshot(
    string Id, OperationsInvestigationStatus Status, string Title, string Objective,
    IReadOnlyList<OperationsInvestigationEvidenceReference> EvidenceReferences,
    IReadOnlyList<OperationsInvestigationClarification> Clarifications,
    OperationsInvestigationProposal? Proposal, long Version = 0,
    OperationsInvestigationExecutionStatus ExecutionStatus = OperationsInvestigationExecutionStatus.NotStarted,
    OperationsInvestigationVerificationStatus VerificationStatus = OperationsInvestigationVerificationStatus.NotStarted,
    string? ResultSummary = null, DateTimeOffset? UpdatedAtUtc = null, string? Notice = null);
public sealed record OperationsInvestigationWorkItem(
    string Id, string Title, string Objective, OperationsInvestigationStatus Status, long Version, DateTimeOffset UpdatedAtUtc);
public sealed record OperationsInvestigationProposalReview(
    string InvestigationId, string ProposalId, bool Approve, string Reason, long ExpectedVersion);
public sealed record OperationsInvestigationExecutionCheck(string Name, bool Passed, string Summary);
public sealed record OperationsInvestigationExecutionAndVerification(
    string InvestigationId, long Version, OperationsInvestigationExecutionStatus ExecutionStatus,
    OperationsInvestigationVerificationStatus VerificationStatus, string? ResultSummary,
    IReadOnlyList<OperationsInvestigationExecutionCheck> Checks, DateTimeOffset UpdatedAtUtc, string? Notice);
