using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Infrastructure.Classification;

/// <summary>Explicit machine scope for administrative follow-up review. Disabled by default.</summary>
public sealed class EllaiFollowUpOptions
{
    public bool Enabled { get; set; }
    public string[] Organizations { get; set; } = [];

    public void Validate()
    {
        if (Organizations is null || Organizations.Length is < 1 or > 100 ||
            Organizations.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 120 || value != value.Trim()) ||
            Organizations.Distinct(StringComparer.Ordinal).Count() != Organizations.Length)
            throw new InvalidOperationException("Follow-up review requires a bounded explicit organization scope.");
    }
}

/// <summary>Records one administrative recommendation from a staff-set overdue schedule.
/// It does not change a follow-up, infer urgency, assign an officer, or send a message.</summary>
public sealed class EllaiFollowUpWork(SwiftAidDbContext db, IOptions<EllaiFollowUpOptions> configuration)
{
    public async Task<EllaiFencedWorkOutcome> RunOneAsync(EllaiOperationalLease lease, Guid runId,
        Guid workTurnId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.RoleId != "ops.followup" || runId == Guid.Empty || workTurnId == Guid.Empty || runId == workTurnId)
            throw new ArgumentException("A follow-up lease and distinct work identities are required.");
        var options = configuration.Value;
        if (!options.Enabled) throw new InvalidOperationException("The scoped follow-up worker is disabled.");
        options.Validate();
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        return await schedule.ExecuteFencedAsync(lease, async (_, _, token) =>
        {
            var acknowledgement = await db.Database.SqlQuery<FollowUpCandidate>($"""
                SELECT c.case_id AS "CaseId", c.organization_id AS "OrganizationId",
                    c.aggregate_version AS "CaseVersion", f.follow_up_id AS "FollowUpId",
                    a.aggregate_version AS "ScheduleVersion", a.follow_up_event_id AS "EventId",
                    a.acknowledgement_due_utc AS "DueAtUtc", a.assignment_id AS "AssignmentId"
                FROM health_officer_escalation_assignments a
                JOIN managed_cases c ON c.case_id = a.case_id
                JOIN managed_case_follow_ups f ON f.follow_up_id = a.follow_up_id AND f.case_id = a.case_id
                JOIN managed_case_escalations e ON e.escalation_id = a.escalation_id AND e.case_id = a.case_id
                WHERE c.workspace_id = {lease.WorkspaceId} AND c.organization_id = ANY({options.Organizations})
                    AND c.workflow <> 'Resolved' AND e.status <> 'Closed' AND f.status = 'Scheduled'
                    AND f.assignee_actor_id = a.officer_actor_id AND a.acknowledgement_due_utc < clock_timestamp()
                    AND NOT EXISTS (SELECT 1 FROM ellai_follow_up_recommendations r
                        WHERE r.officer_assignment_id = a.assignment_id AND r.recommendation_kind = 'OfficerAcknowledgementDue')
                ORDER BY a.acknowledgement_due_utc, a.assignment_id FOR SHARE OF c, f, e LIMIT 1
                """).SingleOrDefaultAsync(token).ConfigureAwait(false);
            if (acknowledgement is not null)
            {
                if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({lease.WorkspaceId}, {acknowledgement.OrganizationId}) AS \"Value\"")
                    .SingleAsync(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ellai_follow_up_recommendations
                        (activity_id, case_id, follow_up_id, source_event_id, schedule_version, due_at_utc,
                         source_case_version, policy_version, run_id, work_turn_id, recorded_at_utc, recommendation_kind, officer_assignment_id)
                    VALUES ({Guid.NewGuid()}, {acknowledgement.CaseId}, {acknowledgement.FollowUpId}, {acknowledgement.EventId},
                        {acknowledgement.ScheduleVersion}, {acknowledgement.DueAtUtc}, {acknowledgement.CaseVersion}, {lease.PolicyVersion},
                        {runId}, {workTurnId}, clock_timestamp(), 'OfficerAcknowledgementDue', {acknowledgement.AssignmentId})
                    """, token).ConfigureAwait(false);
                return true;
            }
            var items = await db.Database.SqlQuery<FollowUpCandidate>($"""
                SELECT c.case_id AS "CaseId", c.organization_id AS "OrganizationId",
                    c.aggregate_version AS "CaseVersion", f.follow_up_id AS "FollowUpId",
                    f.current_schedule_version AS "ScheduleVersion", f.current_schedule_event_id AS "EventId",
                    f.due_at_utc AS "DueAtUtc", NULL::uuid AS "AssignmentId"
                FROM managed_case_follow_ups f
                JOIN managed_cases c ON c.case_id = f.case_id
                JOIN case_events e ON e.event_id = f.current_schedule_event_id AND e.case_id = c.case_id
                    AND e.aggregate_version = f.current_schedule_version
                    AND e.event_type IN ('CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded')
                WHERE c.workspace_id = {lease.WorkspaceId} AND c.organization_id = ANY({options.Organizations})
                    AND c.workflow <> 'Resolved' AND f.status IN ('Scheduled', 'Acknowledged')
                    AND f.due_at_utc < clock_timestamp()
                    AND NOT EXISTS (SELECT 1 FROM ellai_follow_up_recommendations r
                        WHERE r.follow_up_id = f.follow_up_id AND r.schedule_version = f.current_schedule_version
                            AND r.recommendation_kind = 'FollowUpDue')
                ORDER BY f.due_at_utc, f.follow_up_id FOR SHARE OF c, f LIMIT 1
                """).ToArrayAsync(token).ConfigureAwait(false);
            var item = items.SingleOrDefault();
            if (item is null) return false;
            if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({lease.WorkspaceId}, {item.OrganizationId}) AS \"Value\"")
                .SingleAsync(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
            var activity = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ellai_follow_up_recommendations
                    (activity_id, case_id, follow_up_id, source_event_id, schedule_version, due_at_utc,
                     source_case_version, policy_version, run_id, work_turn_id, recorded_at_utc)
                VALUES ({activity}, {item.CaseId}, {item.FollowUpId}, {item.EventId}, {item.ScheduleVersion},
                    {item.DueAtUtc}, {item.CaseVersion}, {lease.PolicyVersion}, {runId}, {workTurnId}, clock_timestamp())
                """, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    private sealed class FollowUpCandidate
    {
        public Guid CaseId { get; init; }
        public string OrganizationId { get; init; } = "";
        public long CaseVersion { get; init; }
        public Guid FollowUpId { get; init; }
        public long ScheduleVersion { get; init; }
        public Guid EventId { get; init; }
        public DateTimeOffset DueAtUtc { get; init; }
        public Guid? AssignmentId { get; init; }
    }
}
