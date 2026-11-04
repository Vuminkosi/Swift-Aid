using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Cases;

public enum FollowUpRecommendationReviewOutcome
{
    Recorded,
    Replayed,
    AlreadyReviewed,
    Stale,
    NotFoundOrNotAuthorized,
    Conflict
}

/// <summary>Records staff review of an existing recommendation; it grants no action or assignment authority.</summary>
public interface IFollowUpRecommendationReviewService
{
    Task<FollowUpRecommendationReviewOutcome> ReviewAsync(ActorContext actor, Guid caseId,
        Guid recommendationId, Guid commandId, CancellationToken ct);
}
