using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Infrastructure.Classification;

/// <summary>Explicit organization rules for non-clinical queue suggestions.</summary>
public sealed class EllaiOrganizationOptions
{
    public bool Enabled { get; set; }
    public Dictionary<string, Dictionary<string, string>> SectorQueues { get; set; } = new(StringComparer.Ordinal);

    public void Validate()
    {
        if (SectorQueues is null || SectorQueues.Count is < 1 or > 100 ||
            SectorQueues.Any(organization => !Valid(organization.Key, 120) || organization.Value is null ||
                organization.Value.Count is < 1 or > 20 || organization.Value.Any(rule =>
                    !Enum.TryParse<SwiftAid.Contracts.Commands.Sector>(rule.Key, false, out var sector) ||
                    !Enum.IsDefined(sector) || !Valid(rule.Value, 100))))
            throw new InvalidOperationException("Organization suggestions require bounded organization, sector and queue rules.");
    }

    private static bool Valid(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && value == value.Trim();
}

/// <summary>Creates one auditable suggestion; never assigns a case or changes its evidence.</summary>
public sealed class EllaiOrganizationWork(SwiftAidDbContext db, IOptions<EllaiOrganizationOptions> configuration)
{
    public async Task<EllaiFencedWorkOutcome> RunOneAsync(EllaiOperationalLease lease, Guid runId, Guid workTurnId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.RoleId != "ops.organization" || runId == Guid.Empty || workTurnId == Guid.Empty || runId == workTurnId)
            throw new ArgumentException("An organization lease and distinct work identities are required.");
        var options = configuration.Value;
        if (!options.Enabled) throw new InvalidOperationException("Organization suggestions are disabled.");
        options.Validate();
        var rules = options.SectorQueues.SelectMany(org => org.Value.Select(rule => (Organization: org.Key, Sector: rule.Key, Queue: rule.Value))).ToArray();
        var organizations = rules.Select(rule => rule.Organization).ToArray();
        var sectors = rules.Select(rule => rule.Sector).ToArray();
        var queues = rules.Select(rule => rule.Queue).ToArray();
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        return await schedule.ExecuteFencedAsync(lease, async (_, _, token) =>
        {
            var candidate = await db.Database.SqlQuery<OrganizationCandidate>($"""
                SELECT c.case_id AS "CaseId", c.aggregate_version AS "CaseVersion", c.report_revision AS "ReportRevision",
                    e.event_id AS "EventId", rules.queue_code AS "QueueCode"
                FROM unnest({organizations}::text[], {sectors}::text[], {queues}::text[]) AS rules(organization_id, sector, queue_code)
                JOIN managed_cases c ON c.organization_id = rules.organization_id AND c.sector = rules.sector
                JOIN workspaces w ON w.workspace_id = c.workspace_id AND w.status = 'Active'
                JOIN organizations o ON o.organization_id = c.organization_id AND o.status = 'Active'
                JOIN workspace_organizations wo ON wo.workspace_id = c.workspace_id AND wo.organization_id = c.organization_id AND wo.status = 'Active'
                JOIN LATERAL (SELECT event_id FROM case_events WHERE case_id = c.case_id AND report_revision = c.report_revision
                    AND event_type IN ('CaseReported', 'CaseReportCorrected') ORDER BY aggregate_version DESC LIMIT 1) e ON TRUE
                CROSS JOIN LATERAL (SELECT COALESCE((SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                    JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                    WHERE promotion.case_id = c.case_id), c.created_at_utc) AS received_at_utc) report_time
                WHERE c.workspace_id = {lease.WorkspaceId} AND c.workflow <> 'Resolved'
                    AND report_time.received_at_utc BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                    AND NOT EXISTS (SELECT 1 FROM ellai_organization_proposals p WHERE p.case_id = c.case_id
                        AND p.source_case_version = c.aggregate_version AND p.queue_code = rules.queue_code AND p.policy_version = {lease.PolicyVersion})
                ORDER BY report_time.received_at_utc DESC, c.case_id FOR SHARE OF c, w, o, wo LIMIT 1
                """).SingleOrDefaultAsync(token).ConfigureAwait(false);
            if (candidate is null) return false;
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ellai_organization_proposals (activity_id, case_id, source_event_id, source_case_version,
                    source_report_revision, queue_code, policy_version, run_id, work_turn_id, recorded_at_utc)
                VALUES ({Guid.NewGuid()}, {candidate.CaseId}, {candidate.EventId}, {candidate.CaseVersion},
                    {candidate.ReportRevision}, {candidate.QueueCode}, {lease.PolicyVersion}, {runId}, {workTurnId}, clock_timestamp())
                ON CONFLICT (case_id, source_case_version, queue_code, policy_version) DO NOTHING
                """, token).ConfigureAwait(false) == 1;
        }, ct).ConfigureAwait(false);
    }

    private sealed class OrganizationCandidate
    {
        public Guid CaseId { get; init; }
        public Guid EventId { get; init; }
        public long CaseVersion { get; init; }
        public long ReportRevision { get; init; }
        public string QueueCode { get; init; } = "";
    }
}

