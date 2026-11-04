namespace SwiftAid.Contracts.Cases;

/// <summary>A candidate for human review, not an assignment or a professional licence assertion.</summary>
public sealed record EligibleHealthOfficerCandidate(Guid DesignationId, long DesignationVersion, string ActorId, string? DisplayLabel);

public sealed record EligibleHealthOfficerCandidatePage(IReadOnlyList<EligibleHealthOfficerCandidate> Items, bool HasMore);

public enum EligibleHealthOfficerLookupStatus { Available, Denied, Unavailable }

public sealed record EligibleHealthOfficerLookupResult(EligibleHealthOfficerLookupStatus Status, EligibleHealthOfficerCandidatePage? Page = null);

public interface IEligibleHealthOfficerClient
{
    Task<EligibleHealthOfficerLookupResult> GetAsync(Guid caseId, CancellationToken cancellationToken = default);
}
