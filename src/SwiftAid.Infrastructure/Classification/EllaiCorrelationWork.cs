using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Cases.Correlation;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Infrastructure.Classification;

public sealed class EllaiCorrelationOptions
{
    public bool Enabled { get; set; }
    public string[] Organizations { get; set; } = [];
    public int MaximumSeparationDays { get; set; } = 7;

    public void Validate()
    {
        if (Organizations is null || Organizations.Length is < 1 or > 100 ||
            Organizations.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 120 || id != id.Trim()) ||
            Organizations.Distinct(StringComparer.Ordinal).Count() != Organizations.Length || MaximumSeparationDays is < 1 or > 30)
            throw new InvalidOperationException("Correlation requires a bounded organization scope and matching interval.");
    }
}

/// <summary>Writes one unconfirmed candidate from authoritative case facts. No case relationship is created.</summary>
public sealed class EllaiCorrelationWork(SwiftAidDbContext db, IOptions<EllaiCorrelationOptions> configuration)
{
    public async Task<EllaiFencedWorkOutcome> RunOneAsync(EllaiOperationalLease lease, Guid runId, Guid workTurnId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.RoleId != "ops.correlation" || runId == Guid.Empty || workTurnId == Guid.Empty || runId == workTurnId)
            throw new ArgumentException("A correlation lease and distinct work identities are required.");
        var settings = configuration.Value;
        if (!settings.Enabled) throw new InvalidOperationException("Correlation is disabled.");
        settings.Validate();
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        var retry = false;
        var outcome = await schedule.ExecuteFencedAsync(lease, async (_, _, token) =>
        {
            var pair = await db.Database.SqlQuery<CandidateRow>($"""
                SELECT a.case_id AS "FirstCaseId", b.case_id AS "SecondCaseId", a.organization_id AS "OrganizationId",
                    a.report_revision AS "FirstRevision", b.report_revision AS "SecondRevision",
                    ae.event_id AS "FirstEventId", be.event_id AS "SecondEventId", a.sector AS "Sector",
                    atime.received_at_utc AS "FirstReportedAtUtc", btime.received_at_utc AS "SecondReportedAtUtc",
                    ARRAY(SELECT r.region_id FROM managed_case_regions r WHERE r.case_id = a.case_id ORDER BY r.region_id) AS "FirstRegions",
                    ARRAY(SELECT r.region_id FROM managed_case_regions r WHERE r.case_id = b.case_id ORDER BY r.region_id) AS "SecondRegions"
                FROM managed_cases a
                JOIN managed_cases b ON b.workspace_id = a.workspace_id AND b.organization_id = a.organization_id
                    AND b.case_id > a.case_id AND b.sector = a.sector
                JOIN workspaces w ON w.workspace_id = a.workspace_id AND w.status = 'Active'
                JOIN organizations o ON o.organization_id = a.organization_id AND o.status = 'Active'
                JOIN workspace_organizations wo ON wo.workspace_id = a.workspace_id AND wo.organization_id = a.organization_id AND wo.status = 'Active'
                JOIN LATERAL (SELECT event_id FROM case_events WHERE case_id = a.case_id AND report_revision = a.report_revision
                    AND event_type IN ('CaseReported', 'CaseReportCorrected') ORDER BY aggregate_version DESC LIMIT 1) ae ON TRUE
                JOIN LATERAL (SELECT event_id FROM case_events WHERE case_id = b.case_id AND report_revision = b.report_revision
                    AND event_type IN ('CaseReported', 'CaseReportCorrected') ORDER BY aggregate_version DESC LIMIT 1) be ON TRUE
                CROSS JOIN LATERAL (SELECT COALESCE((SELECT r.received_at_utc FROM swiftaid_intake.intake_promotions p
                    JOIN swiftaid_intake.intake_reports r ON r.receipt_id = p.receipt_id WHERE p.case_id = a.case_id), a.created_at_utc) AS received_at_utc) atime
                CROSS JOIN LATERAL (SELECT COALESCE((SELECT r.received_at_utc FROM swiftaid_intake.intake_promotions p
                    JOIN swiftaid_intake.intake_reports r ON r.receipt_id = p.receipt_id WHERE p.case_id = b.case_id), b.created_at_utc) AS received_at_utc) btime
                WHERE a.workspace_id = {lease.WorkspaceId} AND a.organization_id = ANY({settings.Organizations})
                    AND a.workflow <> 'Resolved' AND b.workflow <> 'Resolved'
                    AND atime.received_at_utc BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                    AND btime.received_at_utc BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                    AND abs(extract(epoch FROM (atime.received_at_utc - btime.received_at_utc))) <= {settings.MaximumSeparationDays} * 86400
                    AND EXISTS (SELECT 1 FROM managed_case_regions ar JOIN managed_case_regions br ON br.region_id = ar.region_id
                        WHERE ar.case_id = a.case_id AND br.case_id = b.case_id)
                    AND NOT EXISTS (SELECT 1 FROM ellai_correlation_candidates p WHERE p.first_case_id = a.case_id AND p.second_case_id = b.case_id
                        AND p.first_report_revision = a.report_revision AND p.second_report_revision = b.report_revision AND p.policy_version = {lease.PolicyVersion})
                ORDER BY greatest(atime.received_at_utc, btime.received_at_utc) DESC, a.case_id, b.case_id
                FOR SHARE OF a, b, w, o, wo LIMIT 1
                """).SingleOrDefaultAsync(token).ConfigureAwait(false);
            if (pair is null) return false;
            if (!Enum.TryParse<Sector>(pair.Sector, false, out var sector)) throw new InvalidDataException("Unknown case sector.");
            var first = new CorrelationCaseFacts(pair.FirstCaseId, pair.OrganizationId, lease.WorkspaceId, pair.FirstRevision,
                pair.FirstReportedAtUtc, sector, pair.FirstRegions.ToHashSet(StringComparer.Ordinal), null, null);
            var second = new CorrelationCaseFacts(pair.SecondCaseId, pair.OrganizationId, lease.WorkspaceId, pair.SecondRevision,
                pair.SecondReportedAtUtc, sector, pair.SecondRegions.ToHashSet(StringComparer.Ordinal), null, null);
            var candidate = CaseCorrelationMatcher.Compare(first, second, DateTimeOffset.UtcNow, TimeSpan.FromDays(settings.MaximumSeparationDays));
            if (candidate is null) { retry = true; return false; }
            var reasons = candidate.Reasons.Select(reason => reason.ToString()).ToArray();
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ellai_correlation_candidates (candidate_id, workspace_id, organization_id, first_case_id, second_case_id,
                    first_report_revision, second_report_revision, first_event_id, second_event_id, reason_codes,
                    policy_version, run_id, work_turn_id, recorded_at_utc)
                VALUES ({Guid.NewGuid()}, {lease.WorkspaceId}, {pair.OrganizationId}, {pair.FirstCaseId}, {pair.SecondCaseId},
                    {pair.FirstRevision}, {pair.SecondRevision}, {pair.FirstEventId}, {pair.SecondEventId}, {reasons},
                    {lease.PolicyVersion}, {runId}, {workTurnId}, clock_timestamp())
                ON CONFLICT (first_case_id, second_case_id, first_report_revision, second_report_revision, policy_version) DO NOTHING
                """, token).ConfigureAwait(false);
            if (inserted == 0) { retry = true; return false; }
            return true;
        }, ct).ConfigureAwait(false);
        return outcome == EllaiFencedWorkOutcome.NoChange && retry ? EllaiFencedWorkOutcome.Retry : outcome;
    }

    private sealed class CandidateRow
    {
        public Guid FirstCaseId { get; init; }
        public Guid SecondCaseId { get; init; }
        public string OrganizationId { get; init; } = "";
        public long FirstRevision { get; init; }
        public long SecondRevision { get; init; }
        public Guid FirstEventId { get; init; }
        public Guid SecondEventId { get; init; }
        public string Sector { get; init; } = "";
        public DateTimeOffset FirstReportedAtUtc { get; init; }
        public DateTimeOffset SecondReportedAtUtc { get; init; }
        public string[] FirstRegions { get; init; } = [];
        public string[] SecondRegions { get; init; } = [];
    }
}
