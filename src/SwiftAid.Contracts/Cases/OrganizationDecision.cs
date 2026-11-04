namespace SwiftAid.Contracts.Cases;

public enum OrganizationDecision { Accept, Reject }
public enum OrganizationDecisionOutcome { Recorded, Replayed, AlreadyDecided, Stale, Invalid, NotFoundOrNotAuthorized, Conflict, Unavailable }
public sealed record OrganizationDecisionRequest(
    [property: System.Text.Json.Serialization.JsonRequired] Guid CommandId,
    [property: System.Text.Json.Serialization.JsonRequired] OrganizationDecision Decision,
    [property: System.Text.Json.Serialization.JsonRequired] string Rationale);
public sealed record OrganizationDecisionResult(OrganizationDecisionOutcome Status);

/// <summary>Records a review decision only; it does not assign a case.</summary>
public interface IOrganizationSuggestionDecisionClient
{
    Task<OrganizationDecisionResult> DecideAsync(Guid caseId, Guid suggestionId, OrganizationDecisionRequest request,
        CancellationToken cancellationToken = default);
}
