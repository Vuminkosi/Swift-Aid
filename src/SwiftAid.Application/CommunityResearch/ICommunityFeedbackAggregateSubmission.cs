using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.CommunityResearch;

public enum CommunityAggregateSubmissionStatus { Recorded, Replayed, Invalid, NotFoundOrNotAuthorized, Conflict }

/// <summary>Submits a human-attested aggregate; submission does not constitute research approval.</summary>
public interface ICommunityFeedbackAggregateSubmission
{
    Task<CommunityAggregateSubmissionStatus> SubmitAsync(ActorContext actor, CommunityFeedbackAggregate aggregate, CancellationToken ct);
}
