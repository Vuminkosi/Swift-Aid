namespace SwiftAid.Contracts.Cases;

public enum FollowUpReviewStatus { Recorded, Replayed, AlreadyReviewed, Stale, Denied, Conflict, Unavailable }
public sealed record FollowUpReviewRequest(Guid CommandId);
public sealed record FollowUpReviewResult(FollowUpReviewStatus Status);

/// <summary>Records review only; it never changes a follow-up or submits an escalation.</summary>
public interface IFollowUpRecommendationReviewClient
{
    Task<FollowUpReviewResult> ReviewAsync(Guid caseId, Guid recommendationId, Guid commandId,
        CancellationToken cancellationToken = default);
}
