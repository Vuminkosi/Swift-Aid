namespace SwiftAid.Client.Presentation;

public enum InvestigationStatus
{
    NotStarted,
    GatheringEvidence,
    NeedsInput,
    Paused,
    AwaitingApproval,
    Resuming,
    Verifying,
    Partial,
    Stale,
    BudgetExceeded,
    Completed,
    Unavailable,
    Failed
}

public sealed record InvestigationRequest(string Title, string Objective);

public sealed record InvestigationEvidenceReference(string Id, string Label, string SourceVersion);

public sealed record InvestigationClarification(string Id, string FactualQuestion, string? Answer = null);

public sealed record InvestigationProposal(
    string Id,
    string Summary,
    bool RequiresHumanApproval,
    bool Executed,
    string? ReviewStatus = null,
    string? ReviewReason = null);

public enum InvestigationExecutionStatus
{
    NotStarted,
    AwaitingReview,
    InProgress,
    Partial,
    Completed,
    Failed,
    Stale,
    BudgetExceeded
}

public enum InvestigationVerificationStatus
{
    NotStarted,
    InProgress,
    Passed,
    Failed,
    Incomplete
}

public sealed record InvestigationSnapshot(
    string Id,
    InvestigationStatus Status,
    string Title,
    string Objective,
    IReadOnlyList<InvestigationEvidenceReference> EvidenceReferences,
    IReadOnlyList<InvestigationClarification> Clarifications,
    InvestigationProposal? Proposal,
    bool IsSynthetic,
    bool DemoApprovalRecorded = false,
    long Version = 0,
    InvestigationExecutionStatus ExecutionStatus = InvestigationExecutionStatus.NotStarted,
    InvestigationVerificationStatus VerificationStatus = InvestigationVerificationStatus.NotStarted,
    string? ResultSummary = null,
    DateTimeOffset? UpdatedAtUtc = null,
    string? Notice = null);

public interface IAiInvestigationClient
{
    Task<UiCapabilityResult<Page<InvestigationWorkItem>>> ListAsync(
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<Page<InvestigationWorkItem>>("investigations"));
    }

    Task<UiCapabilityResult<InvestigationSnapshot>> GetAsync(
        string investigationId,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<InvestigationSnapshot>> StartAsync(
        InvestigationRequest request,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<InvestigationSnapshot>> ReplyAsync(
        string investigationId,
        long expectedVersion,
        string clarificationId,
        string factualAnswer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<InvestigationSnapshot>("investigations"));
    }

    Task<UiCapabilityResult<InvestigationSnapshot>> PauseAsync(
        string investigationId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<InvestigationSnapshot>("investigations"));
    }

    Task<UiCapabilityResult<InvestigationSnapshot>> ResumeAsync(
        string investigationId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<InvestigationSnapshot>("investigations"));
    }

    Task<UiCapabilityResult<InvestigationSnapshot>> ProposeAdministrativeActionAsync(
        string investigationId,
        long expectedVersion,
        string summary,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<InvestigationSnapshot>("investigations"));
    }

    Task<UiCapabilityResult<InvestigationSnapshot>> ReviewProposalAsync(
        InvestigationProposalReview review,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(review);
        return Task.FromResult(UiCapabilityResults.Unavailable<InvestigationSnapshot>("investigation-proposal-review"));
    }

    Task<UiCapabilityResult<InvestigationExecutionAndVerification>> GetExecutionAndVerificationAsync(
        string investigationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<InvestigationExecutionAndVerification>("investigation-execution-verification"));
    }

}
