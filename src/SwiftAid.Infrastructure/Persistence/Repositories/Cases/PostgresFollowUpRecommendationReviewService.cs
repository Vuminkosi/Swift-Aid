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
        // Reviews are separate immutable artifacts. A read lease keeps the case
        // authority stable while remaining compatible with the scheduled reader.
        try { lease = await unitOfWork.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Read, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized; }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized; }
        await using (lease.ConfigureAwait(false))
        {
            var decision = await policy.AuthorizeAllAsync(actor, caseId,
                [CaseOperation.ReadCase, CaseOperation.ManageFollowUp], ct).ConfigureAwait(false);
            if (!decision.IsAllowed) return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized;
            var commandLock = $"ellai-follow-up-review:{commandId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({commandLock}, 0))", ct).ConfigureAwait(false);
            var previous = await db.Database.SqlQuery<ReviewRow>($"""
                SELECT recommendation_id AS "RecommendationId", case_id AS "CaseId", actor_id AS "ActorId"
                FROM ellai_follow_up_reviews WHERE command_id = {commandId}
                UNION ALL
                SELECT recommendation_id AS "RecommendationId", case_id AS "CaseId", actor_id AS "ActorId"
                FROM ellai_clarification_reviews WHERE command_id = {commandId}
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (previous is not null && (previous.RecommendationId != recommendationId || previous.CaseId != caseId || previous.ActorId != actor.ActorId))
                return FollowUpRecommendationReviewOutcome.Conflict;
            var kind = await db.Database.SqlQuery<string>($"""
                SELECT recommendation_kind AS "Value" FROM ellai_follow_up_recommendations
                WHERE activity_id = {recommendationId} AND case_id = {caseId}
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (kind is null)
            {
                var clarification = await db.Database.SqlQuery<RecommendationRow>($"""
                    SELECT (c.report_revision = source.report_revision AND c.workflow <> 'Resolved') AS "IsCurrent",
                        EXISTS (SELECT 1 FROM ellai_clarification_reviews review WHERE review.recommendation_id = p.activity_id) AS "IsReviewed"
                    FROM ellai_clarification_proposals p
                    JOIN managed_cases c ON c.case_id = p.case_id
                    JOIN case_events source ON source.event_id = p.source_event_id AND source.case_id = p.case_id
                    WHERE p.activity_id = {recommendationId} AND p.case_id = {caseId}
                    """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (clarification is null) return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized;
                if (previous is not null) return FollowUpRecommendationReviewOutcome.Replayed;
                if (clarification.IsReviewed) return FollowUpRecommendationReviewOutcome.AlreadyReviewed;
                if (!clarification.IsCurrent) return FollowUpRecommendationReviewOutcome.Stale;
                if (!await LockCurrentPolicyAsync(caseId, recommendationId, true, ct).ConfigureAwait(false)) return FollowUpRecommendationReviewOutcome.Stale;
                var added = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ellai_clarification_reviews (recommendation_id, case_id, command_id, actor_id, recorded_at_utc)
                    VALUES ({recommendationId}, {caseId}, {commandId}, {actor.ActorId}, clock_timestamp())
                    ON CONFLICT DO NOTHING
                    """, ct).ConfigureAwait(false);
                if (added == 0) return FollowUpRecommendationReviewOutcome.Conflict;
                await lease.CommitAsync(ct).ConfigureAwait(false);
                return FollowUpRecommendationReviewOutcome.Recorded;
            }
            if (kind == "OfficerAcknowledgementDue")
            {
                var officerDecision = await policy.AuthorizeAllAsync(actor, caseId,
                    [CaseOperation.ReadCase, CaseOperation.ManageFollowUp, CaseOperation.ManageEscalation], ct).ConfigureAwait(false);
                if (!officerDecision.IsAllowed) return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized;
            }
            if (previous is not null) return FollowUpRecommendationReviewOutcome.Replayed;
            var current = await db.Database.SqlQuery<RecommendationRow>($"""
                SELECT CASE WHEN r.recommendation_kind = 'OfficerAcknowledgementDue' THEN
                    (f.assignee_actor_id = a.officer_actor_id AND f.status = 'Scheduled'
                    AND f.acknowledged_version IS DISTINCT FROM 0
                    AND (f.current_schedule_version = a.aggregate_version OR f.acknowledged_version = 1)
                    AND e.status <> 'Closed' AND c.workflow <> 'Resolved')
                    ELSE (f.current_schedule_version = r.schedule_version AND f.current_schedule_event_id = r.source_event_id
                    AND f.due_at_utc = r.due_at_utc AND f.status IN ('Scheduled', 'Acknowledged') AND c.workflow <> 'Resolved') END AS "IsCurrent",
                    EXISTS (SELECT 1 FROM ellai_follow_up_reviews review WHERE review.recommendation_id = r.activity_id) AS "IsReviewed"
                FROM ellai_follow_up_recommendations r
                JOIN managed_case_follow_ups f ON f.follow_up_id = r.follow_up_id AND f.case_id = r.case_id
                JOIN managed_cases c ON c.case_id = r.case_id
                LEFT JOIN health_officer_escalation_assignments a ON a.assignment_id = r.officer_assignment_id
                LEFT JOIN managed_case_escalations e ON e.escalation_id = a.escalation_id AND e.case_id = a.case_id
                WHERE r.activity_id = {recommendationId} AND r.case_id = {caseId}
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (current is null) return FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized;
            if (current.IsReviewed) return FollowUpRecommendationReviewOutcome.AlreadyReviewed;
            if (!current.IsCurrent) return FollowUpRecommendationReviewOutcome.Stale;
            if (!await LockCurrentPolicyAsync(caseId, recommendationId, false, ct).ConfigureAwait(false)) return FollowUpRecommendationReviewOutcome.Stale;
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

    private Task<bool> LockCurrentPolicyAsync(Guid caseId, Guid recommendationId, bool clarification, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT TRUE AS "Value" FROM managed_cases c
            JOIN ellai_operational_schedules schedule ON schedule.workspace_id = c.workspace_id AND schedule.role_id = 'ops.followup'
            WHERE c.case_id = {caseId}
                AND COALESCE((SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                    JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                    WHERE promotion.case_id = c.case_id), c.created_at_utc)
                    BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                AND (({clarification} AND EXISTS (SELECT 1 FROM ellai_clarification_proposals p
                    WHERE p.activity_id = {recommendationId} AND p.case_id = c.case_id AND p.policy_version = schedule.policy_version))
                  OR (NOT {clarification} AND EXISTS (SELECT 1 FROM ellai_follow_up_recommendations r
                    WHERE r.activity_id = {recommendationId} AND r.case_id = c.case_id AND r.policy_version = schedule.policy_version)))
            LIMIT 1 FOR SHARE OF schedule
            """).SingleOrDefaultAsync(ct);

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
