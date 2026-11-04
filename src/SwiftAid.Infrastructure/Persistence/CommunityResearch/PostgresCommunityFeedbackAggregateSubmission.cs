using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.CommunityResearch;

namespace SwiftAid.Infrastructure.Persistence.CommunityResearch;

/// <summary>Locks current organization-owner authority and the source session before recording an aggregate.</summary>
public sealed class PostgresCommunityFeedbackAggregateSubmission(SwiftAidDbContext db, TimeProvider clock)
    : ICommunityFeedbackAggregateSubmission
{
    public async Task<CommunityAggregateSubmissionStatus> SubmitAsync(ActorContext actor, CommunityFeedbackAggregate aggregate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(aggregate);
        var now = clock.GetUtcNow();
        if (!CommunityFeedbackAggregateRules.IsValid(aggregate, now)) return CommunityAggregateSubmissionStatus.Invalid;
        // PostgreSQL timestamps are stored at microsecond precision.
        var start = new DateTimeOffset(aggregate.WindowStartUtc.Ticks - aggregate.WindowStartUtc.Ticks % 10, TimeSpan.Zero);
        var end = new DateTimeOffset(aggregate.WindowEndUtc.Ticks - aggregate.WindowEndUtc.Ticks % 10, TimeSpan.Zero);
        if (end <= start) return CommunityAggregateSubmissionStatus.Invalid;
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var authority = await db.Database.SqlQuery<AuthorityRow>($"""
            SELECT member.membership_id AS "MembershipId" FROM community_sessions s
            JOIN organizations o ON o.organization_id = s.organization_id AND o.status = 'Active'
            JOIN organization_owners owner ON owner.organization_id = s.organization_id AND owner.actor_id = {actor.ActorId} AND owner.revoked_at_utc IS NULL
            JOIN organization_memberships member ON member.organization_id = s.organization_id AND member.actor_id = owner.actor_id
                AND member.status = 'Active' AND member.valid_from_utc <= clock_timestamp()
                AND (member.expires_at_utc IS NULL OR member.expires_at_utc > clock_timestamp())
            JOIN actors a ON a.actor_id = owner.actor_id AND a.status = 'Active' AND (a.expires_at_utc IS NULL OR a.expires_at_utc > clock_timestamp())
            JOIN actor_access_states access ON access.actor_id = a.actor_id AND access.access_epoch = {actor.AccessEpoch}
            WHERE s.session_id = {aggregate.SessionId} AND s.organization_id = {aggregate.OrganizationId}
                AND s.status = 'Completed' AND s.version = {aggregate.SessionVersion}
                AND s.topic_tags ? {aggregate.TopicCode}
            ORDER BY member.membership_id LIMIT 1 FOR SHARE OF s, o, owner, member, a, access
            """).ToArrayAsync(ct).ConfigureAwait(false);
        if (authority.Length != 1) return CommunityAggregateSubmissionStatus.NotFoundOrNotAuthorized;
        var commandLock = $"community-feedback-aggregate:{aggregate.CommandId:D}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({commandLock}, 0))", ct).ConfigureAwait(false);
        var prior = await db.Database.SqlQuery<ExistingRow>($"""
            SELECT aggregate_id AS "AggregateId", organization_id AS "OrganizationId", session_id AS "SessionId",
                session_version AS "SessionVersion", topic_code AS "TopicCode", window_start_utc AS "WindowStartUtc",
                window_end_utc AS "WindowEndUtc", respondent_count AS "RespondentCount", mention_count AS "MentionCount",
                submitted_by AS "SubmittedBy"
            FROM community_research_aggregates WHERE command_id = {aggregate.CommandId}
            """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (prior is not null)
            return prior.AggregateId == aggregate.AggregateId && prior.OrganizationId == aggregate.OrganizationId &&
                prior.SessionId == aggregate.SessionId && prior.SessionVersion == aggregate.SessionVersion && prior.TopicCode == aggregate.TopicCode &&
                prior.WindowStartUtc == start && prior.WindowEndUtc == end &&
                prior.RespondentCount == aggregate.RespondentCount && prior.MentionCount == aggregate.MentionCount && prior.SubmittedBy == actor.ActorId
                ? CommunityAggregateSubmissionStatus.Replayed : CommunityAggregateSubmissionStatus.Conflict;
        if (!CommunityFeedbackAggregateRules.IsValid(aggregate, clock.GetUtcNow())) return CommunityAggregateSubmissionStatus.Invalid;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO community_research_aggregates (aggregate_id, command_id, organization_id, session_id, session_version, topic_code,
                window_start_utc, window_end_utc, respondent_count, mention_count, submitted_by, submitted_at_utc)
            VALUES ({aggregate.AggregateId}, {aggregate.CommandId}, {aggregate.OrganizationId}, {aggregate.SessionId}, {aggregate.SessionVersion},
                {aggregate.TopicCode}, {start}, {end}, {aggregate.RespondentCount}, {aggregate.MentionCount},
                {actor.ActorId}, clock_timestamp()) ON CONFLICT DO NOTHING
            """, ct).ConfigureAwait(false);
        if (inserted == 0) return CommunityAggregateSubmissionStatus.Conflict;
        // Share locks prevent revocation, but an expiry can pass while the transaction is open.
        var stillCurrent = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM organization_owners owner
                JOIN organization_memberships member ON member.organization_id = owner.organization_id AND member.actor_id = owner.actor_id
                    AND member.status = 'Active' AND member.valid_from_utc <= clock_timestamp()
                    AND (member.expires_at_utc IS NULL OR member.expires_at_utc > clock_timestamp())
                JOIN actors a ON a.actor_id = owner.actor_id AND a.status = 'Active'
                    AND (a.expires_at_utc IS NULL OR a.expires_at_utc > clock_timestamp())
                WHERE owner.organization_id = {aggregate.OrganizationId} AND owner.actor_id = {actor.ActorId} AND owner.revoked_at_utc IS NULL
                    AND member.membership_id = {authority[0].MembershipId}
            ) AS "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
        if (!stillCurrent || !CommunityFeedbackAggregateRules.IsValid(aggregate, clock.GetUtcNow()))
            return CommunityAggregateSubmissionStatus.NotFoundOrNotAuthorized;
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return CommunityAggregateSubmissionStatus.Recorded;
    }

    private sealed class ExistingRow
    {
        public Guid AggregateId { get; init; }
        public string OrganizationId { get; init; } = "";
        public Guid SessionId { get; init; }
        public long SessionVersion { get; init; }
        public string TopicCode { get; init; } = "";
        public DateTimeOffset WindowStartUtc { get; init; }
        public DateTimeOffset WindowEndUtc { get; init; }
        public int RespondentCount { get; init; }
        public int MentionCount { get; init; }
        public string SubmittedBy { get; init; } = "";
    }

    private sealed class AuthorityRow
    {
        public Guid MembershipId { get; init; }
    }
}
