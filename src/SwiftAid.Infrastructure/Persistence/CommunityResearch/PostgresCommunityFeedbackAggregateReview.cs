using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.CommunityResearch;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Infrastructure.Persistence.CommunityResearch;

/// <summary>Records an owner review of an attested aggregate without changing the source session.</summary>
public sealed class PostgresCommunityFeedbackAggregateReview(SwiftAidDbContext db, IDataProtectionProvider protection)
    : ICommunityFeedbackAggregateReview
{
    public async Task<OrganizationDecisionOutcome> ReviewAsync(ActorContext actor, Guid aggregateId, CommunityAggregateReviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (request is null || aggregateId == Guid.Empty || request.CommandId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.OrganizationId) || request.OrganizationId.Length > 120 || request.OrganizationId != request.OrganizationId.Trim() ||
            !Enum.IsDefined(request.Decision) || string.IsNullOrWhiteSpace(request.Rationale) || request.Rationale.Length > 500 || request.Rationale != request.Rationale.Trim())
            return OrganizationDecisionOutcome.Invalid;
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var authority = await db.Database.SqlQuery<AuthorityRow>($"""
            SELECT member.membership_id AS "MembershipId", aggregate.submitted_by AS "SubmittedBy",
                (s.status = 'Completed' AND s.version = aggregate.session_version AND s.topic_tags ? aggregate.topic_code
                    AND aggregate.window_start_utc >= clock_timestamp() - interval '30 days'
                    AND aggregate.window_end_utc <= clock_timestamp()) AS "IsCurrent"
            FROM community_research_aggregates aggregate
            JOIN community_sessions s ON s.session_id = aggregate.session_id AND s.organization_id = aggregate.organization_id
            JOIN organizations o ON o.organization_id = aggregate.organization_id AND o.status = 'Active'
            JOIN organization_owners owner ON owner.organization_id = aggregate.organization_id AND owner.actor_id = {actor.ActorId} AND owner.revoked_at_utc IS NULL
            JOIN organization_memberships member ON member.organization_id = owner.organization_id AND member.actor_id = owner.actor_id
                AND member.status = 'Active' AND member.valid_from_utc <= clock_timestamp()
                AND (member.expires_at_utc IS NULL OR member.expires_at_utc > clock_timestamp())
            JOIN actors a ON a.actor_id = owner.actor_id AND a.status = 'Active' AND (a.expires_at_utc IS NULL OR a.expires_at_utc > clock_timestamp())
            JOIN actor_access_states access ON access.actor_id = a.actor_id AND access.access_epoch = {actor.AccessEpoch}
            WHERE aggregate.aggregate_id = {aggregateId} AND aggregate.organization_id = {request.OrganizationId}
            ORDER BY member.membership_id LIMIT 1 FOR SHARE OF s, o, owner, member, a, access
            """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (authority is null) return OrganizationDecisionOutcome.NotFoundOrNotAuthorized;
        if (authority.SubmittedBy == actor.ActorId && !request.AcknowledgeSelfReview) return OrganizationDecisionOutcome.Invalid;
        var lockName = $"community-research-review:{request.CommandId:D}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", ct).ConfigureAwait(false);
        var previous = await db.Database.SqlQuery<ReviewRow>($"""
            SELECT aggregate_id AS "AggregateId", reviewer_actor_id AS "ActorId", decision AS "Decision",
                self_review_acknowledged AS "SelfReviewAcknowledged", protected_rationale AS "ProtectedRationale"
            FROM community_research_reviews WHERE command_id = {request.CommandId}
            """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var protector = protection.CreateProtector("SwiftAid.CommunityResearch.Review.v1", request.OrganizationId, aggregateId.ToString("D"));
        if (previous is not null)
            return previous.AggregateId == aggregateId && previous.ActorId == actor.ActorId && previous.Decision == request.Decision.ToString() &&
                previous.SelfReviewAcknowledged == request.AcknowledgeSelfReview &&
                System.Text.Encoding.UTF8.GetString(protector.Unprotect(previous.ProtectedRationale)) == request.Rationale
                ? OrganizationDecisionOutcome.Replayed : OrganizationDecisionOutcome.Conflict;
        if (!authority.IsCurrent) return OrganizationDecisionOutcome.Stale;
        var protectedReason = protector.Protect(System.Text.Encoding.UTF8.GetBytes(request.Rationale));
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO community_research_reviews (aggregate_id, command_id, reviewer_actor_id, decision, self_review_acknowledged, protected_rationale, recorded_at_utc)
            VALUES ({aggregateId}, {request.CommandId}, {actor.ActorId}, {request.Decision.ToString()}, {request.AcknowledgeSelfReview}, {protectedReason}, clock_timestamp())
            ON CONFLICT DO NOTHING
            """, ct).ConfigureAwait(false);
        if (inserted == 0) return OrganizationDecisionOutcome.AlreadyDecided;
        var current = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM organization_memberships member JOIN actors a ON a.actor_id = member.actor_id
                JOIN community_research_aggregates aggregate ON aggregate.aggregate_id = {aggregateId}
                WHERE member.membership_id = {authority.MembershipId} AND member.status = 'Active'
                    AND member.valid_from_utc <= clock_timestamp() AND (member.expires_at_utc IS NULL OR member.expires_at_utc > clock_timestamp())
                    AND a.status = 'Active' AND (a.expires_at_utc IS NULL OR a.expires_at_utc > clock_timestamp())
                    AND aggregate.window_start_utc >= clock_timestamp() - interval '30 days'
                    AND aggregate.window_end_utc <= clock_timestamp()) AS "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
        if (!current) return OrganizationDecisionOutcome.NotFoundOrNotAuthorized;
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return OrganizationDecisionOutcome.Recorded;
    }

    private sealed class AuthorityRow
    {
        public Guid MembershipId { get; init; }
        public string SubmittedBy { get; init; } = "";
        public bool IsCurrent { get; init; }
    }
    private sealed class ReviewRow
    {
        public Guid AggregateId { get; init; }
        public string ActorId { get; init; } = "";
        public string Decision { get; init; } = "";
        public bool SelfReviewAcknowledged { get; init; }
        public byte[] ProtectedRationale { get; init; } = [];
    }
}
