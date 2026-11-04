namespace SwiftAid.Contracts.Cases;

/// <summary>Unconfirmed relationship between two currently readable cases.</summary>
public sealed record CaseCorrelationView(Guid CandidateId, Guid RelatedCaseId, DateTimeOffset RecordedAtUtc,
    IReadOnlyList<string> Reasons, string Limitation, OrganizationDecision? Decision = null);
public sealed record CaseCorrelationReadResult(bool Allowed, IReadOnlyList<CaseCorrelationView> Candidates);
public interface ICaseCorrelationClient
{
    Task<CaseCorrelationReadResult?> GetAsync(Guid caseId, CancellationToken cancellationToken = default);
    Task<OrganizationDecisionResult> DecideAsync(Guid caseId, Guid candidateId, OrganizationDecisionRequest request,
        CancellationToken cancellationToken = default);
}
