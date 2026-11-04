using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.CommunityResearch;

public sealed record CommunityAggregateReviewRequest(
    [property: System.Text.Json.Serialization.JsonRequired] Guid CommandId,
    [property: System.Text.Json.Serialization.JsonRequired] string OrganizationId,
    [property: System.Text.Json.Serialization.JsonRequired] OrganizationDecision Decision,
    [property: System.Text.Json.Serialization.JsonRequired] bool AcknowledgeSelfReview,
    [property: System.Text.Json.Serialization.JsonRequired] string Rationale);

public interface ICommunityFeedbackAggregateReview
{
    Task<OrganizationDecisionOutcome> ReviewAsync(ActorContext actor, Guid aggregateId, CommunityAggregateReviewRequest request, CancellationToken ct);
}
