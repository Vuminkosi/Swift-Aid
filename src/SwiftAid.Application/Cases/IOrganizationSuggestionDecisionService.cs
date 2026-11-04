using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>Records a staff decision; accepting a suggestion does not itself assign a case.</summary>
public interface IOrganizationSuggestionDecisionService
{
    Task<OrganizationDecisionOutcome> DecideAsync(ActorContext actor, Guid caseId, Guid suggestionId,
        Guid commandId, OrganizationDecision decision, string rationale, CancellationToken ct);
}
