using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases.Correlation;

public interface ICaseCorrelationDecisionService
{
    Task<OrganizationDecisionOutcome> DecideAsync(ActorContext actor, Guid caseId, Guid candidateId,
        Guid commandId, OrganizationDecision decision, string rationale, CancellationToken ct);
}
