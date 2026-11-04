using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Persists an explicit human review under the same case and authority locks as case commands.</summary>
public sealed class PostgresFollowUpRecommendationReviewService(
    SwiftAidDbContext db, ICaseCommandUnitOfWork unitOfWork, ICaseAccessPolicy policy)
    : IFollowUpRecommendationReviewService
{
    public async Task<FollowUpRecommendationReviewOutcome> ReviewAsync(ActorContext actor, Guid caseId,
        Guid recommendationId, Guid commandId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty || recommendationId == Guid.Empty || commandId == Guid.Empty)
            return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized;
        ICaseCommandLease lease;
        try { lease = await unitOfWork.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Write, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized; }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized; }
        await using (lease.ConfigureAwait(false))
        {
            var decision = await policy.AuthorizeAllAsync(actor, caseId,
                [CaseOperation.ReadCase, CaseOperation.ManageFollowUp], ct).ConfigureAwait(false);
            if (!decision.IsAllowed) return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized;
            var previous = await db.Database.SqlQuery<ReviewRow>($"""
                SELECT recommendation_id AS "RecommendationId", case_id AS "CaseId", actor_id AS "ActorId"
                FROM ellai_follow_up_reviews WHERE command_id = {commandId}
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (previous is not null)
                return previous.RecommendationId == recommendationId && previous.CaseId == caseId && previous.ActorId == actor.ActorId
                    ? FollowUpRecommendationReviewOutcome.Replayed : FollowUpRecommendationReviewOutcome.Conflict;
            var current = await db.Database.SqlQuery<RecommendationRow>($"""
                SELECT (f.current_schedule_version = r.schedule_version AND f.current_schedule_event_id = r.source_event_id
                    AND f.due_at_utc = r.due_at_utc AND f.status IN ('Scheduled', 'Acknowledged') AND c.workflow <> 'Resolved') AS "IsCurrent",
                    EXISTS (SELECT 1 FROM ellai_follow_up_reviews review WHERE review.recommendation_id = r.activity_id) AS "IsReviewed"
                FROM ellai_follow_up_recommendations r
                JOIN managed_case_follow_ups f ON f.follow_up_id = r.follow_up_id AND f.case_id = r.case_id
                JOIN managed_cases c ON c.case_id = r.case_id
                WHERE r.activity_id = {recommendationId} AND r.case_id = {caseId}
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (current is null) return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized;
            if (current.IsReviewed) return FollowUpRecommendationReviewOutcome.AlreadyReviewed;
            if (!current.IsCurrent) return FollowUpRecommendationReviewOutcome.Stale;
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ellai_follow_up_reviews (recommendation_id, case_id, command_id, actor_id, recorded_at_utc)
                VALUES ({recommendationId}, {caseId}, {commandId}, {actor.ActorId}, clock_timestamp())
                ON CONFLICT DO NOTHING
                """, ct).ConfigureAwait(false);
            if (inserted == 0) return FollowUpRecommendationReviewOutcome.Conflict;
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return FollowUpRecommendationReviewOutcome.Recorded;
        }
    }

    private sealed class ReviewRow
    {
        public Guid RecommendationId { get; init; }
        public Guid CaseId { get; init; }
        public string ActorId { get; init; } = "";
    }

    private sealed class RecommendationRow
    {
        public bool IsCurrent { get; init; }
        public bool IsReviewed { get; init; }
    }
}
