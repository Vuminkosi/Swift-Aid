using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases.Correlation;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Reads candidates under the source case lease; the caller separately leases and authorizes the other case.</summary>
public sealed class PostgresCaseCorrelationStore(SwiftAidDbContext db) : ICaseCorrelationStore
{
    public async Task<IReadOnlyList<CorrelationScanRow>> GetAfterAsync(Guid caseId, CorrelationCursor? cursor, int limit, CancellationToken ct)
    {
        if (caseId == Guid.Empty || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var beforeTime = cursor?.SortTimeUtc;
        var beforeId = cursor?.CandidateId;
        return await db.Database.SqlQuery<CorrelationScanRow>($"""
            SELECT p.candidate_id AS "CandidateId", p.first_case_id AS "FirstCaseId", p.second_case_id AS "SecondCaseId",
                p.first_report_revision AS "FirstRevision", p.second_report_revision AS "SecondRevision",
                p.recorded_at_utc AS "RecordedAtUtc", p.reason_codes AS "Reasons", d.decision AS "Decision",
                sort_time.reported_at_utc AS "SortTimeUtc"
            FROM ellai_correlation_candidates p LEFT JOIN ellai_correlation_decisions d ON d.candidate_id = p.candidate_id
            JOIN managed_cases a ON a.case_id = p.first_case_id
            JOIN managed_cases b ON b.case_id = p.second_case_id
            CROSS JOIN LATERAL (SELECT COALESCE((SELECT r.received_at_utc FROM swiftaid_intake.intake_promotions i
                JOIN swiftaid_intake.intake_reports r ON r.receipt_id = i.receipt_id WHERE i.case_id = a.case_id), a.created_at_utc) AS received_at_utc) atime
            CROSS JOIN LATERAL (SELECT COALESCE((SELECT r.received_at_utc FROM swiftaid_intake.intake_promotions i
                JOIN swiftaid_intake.intake_reports r ON r.receipt_id = i.receipt_id WHERE i.case_id = b.case_id), b.created_at_utc) AS received_at_utc) btime
            CROSS JOIN LATERAL (SELECT CASE WHEN p.first_case_id = {caseId} THEN btime.received_at_utc ELSE atime.received_at_utc END AS reported_at_utc) sort_time
            WHERE (p.first_case_id = {caseId} OR p.second_case_id = {caseId})
                AND ({beforeTime}::timestamptz IS NULL OR sort_time.reported_at_utc < {beforeTime}::timestamptz
                    OR (sort_time.reported_at_utc = {beforeTime}::timestamptz AND p.candidate_id < {beforeId}::uuid))
            ORDER BY sort_time.reported_at_utc DESC, p.candidate_id DESC LIMIT {limit}
            """).ToArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredCorrelationCandidate>> GetCandidatesAsync(Guid caseId, int limit, CancellationToken ct)
    {
        if (caseId == Guid.Empty || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.Database.SqlQuery<StoredCorrelationCandidate>($"""
            SELECT p.candidate_id AS "CandidateId", p.first_case_id AS "FirstCaseId", p.second_case_id AS "SecondCaseId",
                p.first_report_revision AS "FirstRevision", p.second_report_revision AS "SecondRevision",
                p.recorded_at_utc AS "RecordedAtUtc", p.reason_codes AS "Reasons", d.decision AS "Decision"
            FROM ellai_correlation_candidates p LEFT JOIN ellai_correlation_decisions d ON d.candidate_id = p.candidate_id
            JOIN managed_cases a ON a.case_id = p.first_case_id
            JOIN managed_cases b ON b.case_id = p.second_case_id
            CROSS JOIN LATERAL (SELECT COALESCE((SELECT r.received_at_utc FROM swiftaid_intake.intake_promotions i
                JOIN swiftaid_intake.intake_reports r ON r.receipt_id = i.receipt_id WHERE i.case_id = a.case_id), a.created_at_utc) AS received_at_utc) atime
            CROSS JOIN LATERAL (SELECT COALESCE((SELECT r.received_at_utc FROM swiftaid_intake.intake_promotions i
                JOIN swiftaid_intake.intake_reports r ON r.receipt_id = i.receipt_id WHERE i.case_id = b.case_id), b.created_at_utc) AS received_at_utc) btime
            WHERE p.first_case_id = {caseId} OR p.second_case_id = {caseId}
            ORDER BY CASE WHEN p.first_case_id = {caseId} THEN btime.received_at_utc ELSE atime.received_at_utc END DESC,
                p.candidate_id DESC LIMIT {limit}
            """).ToArrayAsync(ct).ConfigureAwait(false);
    }

    public Task<bool> AreCurrentAsync(StoredCorrelationCandidate candidate, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT TRUE AS "Value" FROM ellai_correlation_candidates p
                JOIN ellai_operational_schedules schedule ON schedule.workspace_id = p.workspace_id
                    AND schedule.role_id = 'ops.correlation' AND schedule.policy_version = p.policy_version
                JOIN managed_cases a ON a.case_id = p.first_case_id AND a.report_revision = p.first_report_revision
                JOIN managed_cases b ON b.case_id = p.second_case_id AND b.report_revision = p.second_report_revision
                JOIN workspaces w ON w.workspace_id = p.workspace_id AND w.status = 'Active'
                JOIN organizations o ON o.organization_id = p.organization_id AND o.status = 'Active'
                JOIN workspace_organizations wo ON wo.workspace_id = p.workspace_id AND wo.organization_id = p.organization_id AND wo.status = 'Active'
                WHERE p.candidate_id = {candidate.CandidateId} AND p.first_case_id = {candidate.FirstCaseId}
                    AND p.second_case_id = {candidate.SecondCaseId} AND p.first_report_revision = {candidate.FirstRevision}
                    AND p.second_report_revision = {candidate.SecondRevision}
                    AND a.organization_id = p.organization_id AND b.organization_id = p.organization_id
                    AND a.workspace_id = p.workspace_id AND b.workspace_id = p.workspace_id
                    AND a.workflow <> 'Resolved' AND b.workflow <> 'Resolved'
                    AND a.sector = b.sector AND EXISTS (
                        SELECT 1 FROM managed_case_regions ar JOIN managed_case_regions br ON br.region_id = ar.region_id
                        WHERE ar.case_id = a.case_id AND br.case_id = b.case_id)
                    AND COALESCE((SELECT r.received_at_utc FROM swiftaid_intake.intake_promotions i
                        JOIN swiftaid_intake.intake_reports r ON r.receipt_id = i.receipt_id WHERE i.case_id = a.case_id), a.created_at_utc)
                        BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                    AND COALESCE((SELECT r.received_at_utc FROM swiftaid_intake.intake_promotions i
                        JOIN swiftaid_intake.intake_reports r ON r.receipt_id = i.receipt_id WHERE i.case_id = b.case_id), b.created_at_utc)
                        BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                LIMIT 1 FOR SHARE OF schedule
            """).SingleOrDefaultAsync(ct);
}
