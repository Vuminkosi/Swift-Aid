using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Infrastructure.Classification;

/// <summary>Explicit machine scope for administrative follow-up review. Disabled by default.</summary>
public sealed class EllaiFollowUpOptions
{
    public bool Enabled { get; set; }
    public string[] Organizations { get; set; } = [];
    public Dictionary<string, string[]> ClarificationFindingCodes { get; set; } = new(StringComparer.Ordinal);

    private static readonly HashSet<string> SupportedClarifications = new(StringComparer.Ordinal)
    {
        "occurrence-not-known", "region-not-known", "incomplete-coordinate-pair", "text-evidence-unavailable"
    };

    public void Validate()
    {
        if (Organizations is null || Organizations.Length is < 1 or > 100 ||
            Organizations.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 120 || value != value.Trim()) ||
            Organizations.Distinct(StringComparer.Ordinal).Count() != Organizations.Length || ClarificationFindingCodes is null ||
            ClarificationFindingCodes.Count > 100 || ClarificationFindingCodes.Any(rule =>
                !Organizations.Contains(rule.Key, StringComparer.Ordinal) || rule.Value is null || rule.Value.Length > 4 ||
                rule.Value.Any(code => code is null || !SupportedClarifications.Contains(code)) ||
                rule.Value.Distinct(StringComparer.Ordinal).Count() != rule.Value.Length))
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
        var retry = false;
        var result = await schedule.ExecuteFencedAsync(lease, async (_, _, token) =>
        {
            var hasScopedCases = await db.ManagedCases.AsNoTracking().AnyAsync(c =>
                c.WorkspaceId == lease.WorkspaceId && options.Organizations.Contains(c.OrganizationId), token)
                .ConfigureAwait(false);
            if (hasScopedCases && !await db.WorkspaceOrganizations.AsNoTracking().AnyAsync(link =>
                link.WorkspaceId == lease.WorkspaceId && options.Organizations.Contains(link.OrganizationId) &&
                link.Status == PersistentResourceStatus.Active &&
                db.Workspaces.Any(w => w.WorkspaceId == link.WorkspaceId && w.Status == PersistentResourceStatus.Active) &&
                db.Organizations.Any(o => o.OrganizationId == link.OrganizationId && o.Status == PersistentResourceStatus.Active), token)
                .ConfigureAwait(false)) throw new UnauthorizedAccessException();

            var acknowledgement = await db.Database.SqlQuery<FollowUpCandidate>($"""
                SELECT c.case_id AS "CaseId", c.organization_id AS "OrganizationId",
                    report_time.received_at_utc AS "CaseCreatedAtUtc", f.follow_up_id AS "FollowUpId",
                    a.aggregate_version AS "ScheduleVersion", a.follow_up_event_id AS "EventId",
                    a.acknowledgement_due_utc AS "DueAtUtc", a.assignment_id AS "AssignmentId"
                FROM health_officer_escalation_assignments a
                JOIN managed_cases c ON c.case_id = a.case_id
                JOIN workspaces w ON w.workspace_id = c.workspace_id AND w.status = 'Active'
                JOIN organizations o ON o.organization_id = c.organization_id AND o.status = 'Active'
                JOIN workspace_organizations wo ON wo.workspace_id = c.workspace_id
                    AND wo.organization_id = c.organization_id AND wo.status = 'Active'
                JOIN managed_case_follow_ups f ON f.follow_up_id = a.follow_up_id AND f.case_id = a.case_id
                JOIN managed_case_escalations e ON e.escalation_id = a.escalation_id AND e.case_id = a.case_id
                CROSS JOIN LATERAL (SELECT COALESCE((
                    SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                    JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                    WHERE promotion.case_id = c.case_id
                ), c.created_at_utc) AS received_at_utc) report_time
                WHERE c.workspace_id = {lease.WorkspaceId} AND c.organization_id = ANY({options.Organizations})
                    AND report_time.received_at_utc BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                    AND c.workflow <> 'Resolved' AND e.status <> 'Closed' AND f.status = 'Scheduled'
                    AND f.acknowledged_version IS DISTINCT FROM 0
                    AND (f.current_schedule_version = a.aggregate_version OR f.acknowledged_version = 1)
                    AND f.assignee_actor_id = a.officer_actor_id AND a.acknowledgement_due_utc < clock_timestamp()
                    AND NOT EXISTS (SELECT 1 FROM ellai_follow_up_recommendations r
                        WHERE r.officer_assignment_id = a.assignment_id AND r.recommendation_kind = 'OfficerAcknowledgementDue')
                ORDER BY report_time.received_at_utc DESC, c.case_id, a.acknowledgement_due_utc, a.assignment_id LIMIT 1
                """).SingleOrDefaultAsync(token).ConfigureAwait(false);
            var items = await db.Database.SqlQuery<FollowUpCandidate>($"""
                SELECT c.case_id AS "CaseId", c.organization_id AS "OrganizationId",
                    report_time.received_at_utc AS "CaseCreatedAtUtc", f.follow_up_id AS "FollowUpId",
                    f.current_schedule_version AS "ScheduleVersion", f.current_schedule_event_id AS "EventId",
                    f.due_at_utc AS "DueAtUtc", NULL::uuid AS "AssignmentId"
                FROM managed_case_follow_ups f
                JOIN managed_cases c ON c.case_id = f.case_id
                JOIN workspaces w ON w.workspace_id = c.workspace_id AND w.status = 'Active'
                JOIN organizations o ON o.organization_id = c.organization_id AND o.status = 'Active'
                JOIN workspace_organizations wo ON wo.workspace_id = c.workspace_id
                    AND wo.organization_id = c.organization_id AND wo.status = 'Active'
                JOIN case_events e ON e.event_id = f.current_schedule_event_id AND e.case_id = c.case_id
                    AND e.aggregate_version = f.current_schedule_version
                    AND e.event_type IN ('CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded')
                CROSS JOIN LATERAL (SELECT COALESCE((
                    SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                    JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                    WHERE promotion.case_id = c.case_id
                ), c.created_at_utc) AS received_at_utc) report_time
                WHERE c.workspace_id = {lease.WorkspaceId} AND c.organization_id = ANY({options.Organizations})
                    AND report_time.received_at_utc BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                    AND c.workflow <> 'Resolved' AND f.status IN ('Scheduled', 'Acknowledged')
                    AND f.due_at_utc < clock_timestamp()
                    AND NOT EXISTS (SELECT 1 FROM ellai_follow_up_recommendations r
                        WHERE r.follow_up_id = f.follow_up_id AND r.schedule_version = f.current_schedule_version
                            AND r.recommendation_kind = 'FollowUpDue')
                ORDER BY report_time.received_at_utc DESC, c.case_id, f.due_at_utc, f.follow_up_id LIMIT 1
                """).ToArrayAsync(token).ConfigureAwait(false);
            var item = items.SingleOrDefault();
            var rules = options.ClarificationFindingCodes.SelectMany(pair => pair.Value.Select(code => (Organization: pair.Key, Code: code))).ToArray();
            ClarificationCandidate? clarification = null;
            if (rules.Length != 0)
            {
                var ruleOrganizations = rules.Select(rule => rule.Organization).ToArray();
                var ruleCodes = rules.Select(rule => rule.Code).ToArray();
                clarification = await db.Database.SqlQuery<ClarificationCandidate>($"""
                    SELECT c.case_id AS "CaseId", c.organization_id AS "OrganizationId",
                        report_time.received_at_utc AS "CaseCreatedAtUtc", e.event_id AS "EventId", rules.code AS "FindingCode"
                    FROM unnest({ruleOrganizations}::text[], {ruleCodes}::text[]) AS rules(organization_id, code)
                    JOIN managed_cases c ON c.organization_id = rules.organization_id
                    JOIN workspaces w ON w.workspace_id = c.workspace_id AND w.status = 'Active'
                    JOIN organizations o ON o.organization_id = c.organization_id AND o.status = 'Active'
                    JOIN workspace_organizations wo ON wo.workspace_id = c.workspace_id
                        AND wo.organization_id = c.organization_id AND wo.status = 'Active'
                    JOIN swiftaid_classification.quality_assessments q ON q."CaseId" = c.case_id
                        AND q."ReportRevision" = c.report_revision AND q."Version" = {ReportQualityRules.Version}
                    JOIN case_events e ON e.event_id = q."SourceEventId" AND e.case_id = c.case_id
                        AND e.report_revision = c.report_revision AND e.event_type IN ('CaseReported', 'CaseReportCorrected')
                    CROSS JOIN LATERAL (SELECT COALESCE((
                        SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                        JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                        WHERE promotion.case_id = c.case_id
                    ), c.created_at_utc) AS received_at_utc) report_time
                    WHERE c.workspace_id = {lease.WorkspaceId} AND c.workflow <> 'Resolved'
                        AND report_time.received_at_utc BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                        AND EXISTS (SELECT 1 FROM jsonb_array_elements(COALESCE(q."AssessmentJson"::jsonb -> 'findings',
                            q."AssessmentJson"::jsonb -> 'Findings', '[]'::jsonb)) finding
                            WHERE COALESCE(finding ->> 'code', finding ->> 'Code') = rules.code)
                        AND NOT EXISTS (SELECT 1 FROM ellai_clarification_proposals p
                            WHERE p.source_event_id = e.event_id AND p.finding_code = rules.code)
                    ORDER BY report_time.received_at_utc DESC, c.case_id, rules.code LIMIT 1
                    """).SingleOrDefaultAsync(token).ConfigureAwait(false);
            }
            if (clarification is not null && IsNewer(clarification, acknowledgement) && IsNewer(clarification, item))
            {
                if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({lease.WorkspaceId}, {clarification.OrganizationId}) AS \"Value\"")
                    .SingleAsync(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
                var current = await db.Database.SqlQuery<long>($"""
                    SELECT c.aggregate_version AS "Value" FROM managed_cases c JOIN case_events e ON e.event_id = {clarification.EventId}
                    WHERE c.case_id = {clarification.CaseId} AND c.workspace_id = {lease.WorkspaceId}
                        AND c.organization_id = {clarification.OrganizationId} AND c.workflow <> 'Resolved'
                        AND e.case_id = c.case_id AND e.report_revision = c.report_revision
                    FOR SHARE OF c
                    """).ToArrayAsync(token).ConfigureAwait(false);
                if (current.Length != 1) { retry = true; return false; }
                var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ellai_clarification_proposals
                        (activity_id, case_id, source_event_id, quality_version, finding_code, source_case_version,
                         policy_version, run_id, work_turn_id, recorded_at_utc)
                    VALUES ({Guid.NewGuid()}, {clarification.CaseId}, {clarification.EventId}, {ReportQualityRules.Version},
                        {clarification.FindingCode}, {current[0]}, {lease.PolicyVersion}, {runId}, {workTurnId}, clock_timestamp())
                    ON CONFLICT (source_event_id, finding_code) DO NOTHING
                    """, token).ConfigureAwait(false);
                if (inserted == 0) { retry = true; return false; }
                return true;
            }
            if (acknowledgement is not null && (item is null ||
                acknowledgement.CaseCreatedAtUtc > item.CaseCreatedAtUtc ||
                acknowledgement.CaseCreatedAtUtc == item.CaseCreatedAtUtc &&
                (string.CompareOrdinal(acknowledgement.CaseId.ToString("D"), item.CaseId.ToString("D")) < 0 ||
                 acknowledgement.CaseId == item.CaseId && acknowledgement.DueAtUtc <= item.DueAtUtc)))
            {
                if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({lease.WorkspaceId}, {acknowledgement.OrganizationId}) AS \"Value\"")
                    .SingleAsync(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
                var current = await db.Database.SqlQuery<long>($"""
                    SELECT c.aggregate_version AS "Value" FROM health_officer_escalation_assignments a
                    JOIN managed_cases c ON c.case_id = a.case_id
                    JOIN managed_case_follow_ups f ON f.follow_up_id = a.follow_up_id AND f.case_id = c.case_id
                    JOIN managed_case_escalations e ON e.escalation_id = a.escalation_id AND e.case_id = c.case_id
                    WHERE a.assignment_id = {acknowledgement.AssignmentId} AND c.case_id = {acknowledgement.CaseId}
                        AND c.workspace_id = {lease.WorkspaceId} AND c.organization_id = {acknowledgement.OrganizationId}
                        AND a.aggregate_version = {acknowledgement.ScheduleVersion} AND a.follow_up_event_id = {acknowledgement.EventId}
                        AND a.acknowledgement_due_utc = {acknowledgement.DueAtUtc} AND a.acknowledgement_due_utc < clock_timestamp()
                        AND c.workflow <> 'Resolved' AND e.status <> 'Closed' AND f.status = 'Scheduled'
                        AND f.acknowledged_version IS DISTINCT FROM 0
                        AND (f.current_schedule_version = a.aggregate_version OR f.acknowledged_version = 1)
                        AND f.assignee_actor_id = a.officer_actor_id
                    FOR SHARE OF c, f, e
                    """).ToArrayAsync(token).ConfigureAwait(false);
                if (current.Length != 1) { retry = true; return false; }
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ellai_follow_up_recommendations
                        (activity_id, case_id, follow_up_id, source_event_id, schedule_version, due_at_utc,
                         source_case_version, policy_version, run_id, work_turn_id, recorded_at_utc, recommendation_kind, officer_assignment_id)
                    VALUES ({Guid.NewGuid()}, {acknowledgement.CaseId}, {acknowledgement.FollowUpId}, {acknowledgement.EventId},
                        {acknowledgement.ScheduleVersion}, {acknowledgement.DueAtUtc}, {current[0]}, {lease.PolicyVersion},
                        {runId}, {workTurnId}, clock_timestamp(), 'OfficerAcknowledgementDue', {acknowledgement.AssignmentId})
                    """, token).ConfigureAwait(false);
                return true;
            }
            if (item is null) return false;
            if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({lease.WorkspaceId}, {item.OrganizationId}) AS \"Value\"")
                .SingleAsync(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
            var currentFollowUp = await db.Database.SqlQuery<long>($"""
                SELECT c.aggregate_version AS "Value" FROM managed_case_follow_ups f
                JOIN managed_cases c ON c.case_id = f.case_id
                JOIN case_events e ON e.event_id = f.current_schedule_event_id AND e.case_id = c.case_id
                    AND e.aggregate_version = f.current_schedule_version
                    AND e.event_type IN ('CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded')
                WHERE f.follow_up_id = {item.FollowUpId} AND c.case_id = {item.CaseId}
                    AND c.workspace_id = {lease.WorkspaceId} AND c.organization_id = {item.OrganizationId}
                    AND f.current_schedule_version = {item.ScheduleVersion} AND f.current_schedule_event_id = {item.EventId}
                    AND f.due_at_utc = {item.DueAtUtc} AND f.due_at_utc < clock_timestamp()
                    AND c.workflow <> 'Resolved' AND f.status IN ('Scheduled', 'Acknowledged')
                FOR SHARE OF c, f
                """).ToArrayAsync(token).ConfigureAwait(false);
            if (currentFollowUp.Length != 1) { retry = true; return false; }
            var activity = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ellai_follow_up_recommendations
                    (activity_id, case_id, follow_up_id, source_event_id, schedule_version, due_at_utc,
                     source_case_version, policy_version, run_id, work_turn_id, recorded_at_utc)
                VALUES ({activity}, {item.CaseId}, {item.FollowUpId}, {item.EventId}, {item.ScheduleVersion},
                    {item.DueAtUtc}, {currentFollowUp[0]}, {lease.PolicyVersion}, {runId}, {workTurnId}, clock_timestamp())
                """, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        return result == EllaiFencedWorkOutcome.NoChange && retry ? EllaiFencedWorkOutcome.Retry : result;
    }

    private static bool IsNewer(ClarificationCandidate candidate, FollowUpCandidate? other) => other is null ||
        candidate.CaseCreatedAtUtc > other.CaseCreatedAtUtc || candidate.CaseCreatedAtUtc == other.CaseCreatedAtUtc &&
        string.CompareOrdinal(candidate.CaseId.ToString("D"), other.CaseId.ToString("D")) < 0;

    private sealed class ClarificationCandidate
    {
        public Guid CaseId { get; init; }
        public string OrganizationId { get; init; } = "";
        public DateTimeOffset CaseCreatedAtUtc { get; init; }
        public Guid EventId { get; init; }
        public string FindingCode { get; init; } = "";
    }

    private sealed class FollowUpCandidate
    {
        public Guid CaseId { get; init; }
        public string OrganizationId { get; init; } = "";
        public DateTimeOffset CaseCreatedAtUtc { get; init; }
        public Guid FollowUpId { get; init; }
        public long ScheduleVersion { get; init; }
        public Guid EventId { get; init; }
        public DateTimeOffset DueAtUtc { get; init; }
        public Guid? AssignmentId { get; init; }
    }
}
