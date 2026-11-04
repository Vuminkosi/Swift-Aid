using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;

namespace SwiftAid.Infrastructure.Classification;

/// <summary>One scheduled quality work turn. Scope comes from the existing
/// classification worker configuration; no model or external communication is used.</summary>
public sealed class EllaiQualityWork(SwiftAidDbContext db, PostgresProtectedCaseEventPayloadStore payloads,
    IOptions<ClassificationOptions> configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EllaiFencedWorkOutcome> RunOneAsync(EllaiOperationalLease lease,
        Guid runId, Guid workTurnId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.RoleId != "ops.quality" || runId == Guid.Empty || workTurnId == Guid.Empty || runId == workTurnId)
            throw new ArgumentException("A quality lease and distinct work identities are required.");
        var options = configuration.Value;
        if (!options.Enabled) throw new InvalidOperationException("The scoped classification worker is disabled.");
        options.Validate();
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        var concurrentInsert = false;
        var result = await schedule.ExecuteFencedAsync(lease, async (_, _, token) =>
        {
            // The source case stays locked while its current facts revision is assessed.
            // Previously assessed revisions are excluded before unprotecting evidence.
            // Intake receipt time takes precedence over promotion time; directly registered
            // cases use their registration time. The window never deletes or changes reports.
            var candidates = await db.Database.SqlQuery<QualityCandidate>($"""
                SELECT c.case_id AS "CaseId", e.event_id AS "EventId", c.aggregate_version AS "CaseVersion"
                FROM managed_cases c
                JOIN workspaces w ON w.workspace_id = c.workspace_id AND w.status = 'Active'
                JOIN organizations o ON o.organization_id = c.organization_id AND o.status = 'Active'
                JOIN workspace_organizations wo ON wo.workspace_id = c.workspace_id
                    AND wo.organization_id = c.organization_id AND wo.status = 'Active'
                JOIN LATERAL (
                    SELECT event_id FROM case_events
                    WHERE case_id = c.case_id AND report_revision = c.report_revision
                        AND event_type IN ('CaseReported', 'CaseReportCorrected')
                    ORDER BY aggregate_version DESC LIMIT 1
                ) e ON TRUE
                WHERE c.workspace_id = {lease.WorkspaceId} AND c.organization_id = ANY({options.Organizations})
                    AND COALESCE((
                        SELECT report.received_at_utc
                        FROM swiftaid_intake.intake_promotions promotion
                        JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                        WHERE promotion.case_id = c.case_id
                    ), c.created_at_utc) BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
                    AND NOT EXISTS (
                        SELECT 1 FROM swiftaid_classification.quality_assessments q
                        WHERE q."SourceEventId" = e.event_id AND q."Version" = {ReportQualityRules.Version})
                ORDER BY c.created_at_utc DESC, c.case_id FOR SHARE OF c, w, o, wo LIMIT 1
                """).ToArrayAsync(token).ConfigureAwait(false);
            var candidate = candidates.SingleOrDefault();
            if (candidate is null) return false;
            var source = await db.CaseEvents.AsNoTracking().SingleAsync(item => item.EventId == candidate.EventId &&
                item.CaseId == candidate.CaseId, token).ConfigureAwait(false);
            var bytes = await payloads.UnprotectForClassificationAsync(source, options.Organizations, token).ConfigureAwait(false);
            ReportQualityAssessment quality;
            try
            {
                var facts = CaseEventJournalReader.Read(source, bytes) switch
                {
                    CaseReported reported => reported.ReportData,
                    CaseReportCorrected corrected => corrected.CorrectedReportData,
                    _ => throw new InvalidDataException("The source event is not a report revision.")
                };
                quality = ReportQualityRules.Assess(facts, source.RecordedAtUtc);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO swiftaid_classification.quality_assessments
                    ("SourceEventId", "Version", "CaseId", "ReportRevision", "AssessmentJson", "CreatedAtUtc")
                VALUES ({source.EventId}, {ReportQualityRules.Version}, {source.CaseId}, {source.ReportRevision},
                    {JsonSerializer.Serialize(quality, Json)}::jsonb, clock_timestamp())
                ON CONFLICT ("SourceEventId", "Version") DO NOTHING
                """, token).ConfigureAwait(false);
            if (inserted == 0)
            {
                // Another classifier won this source revision after candidate selection.
                // It is not evidence that the workspace has no other due work.
                concurrentInsert = true;
                return false;
            }
            var fingerprint = ClassificationFingerprint.Hash($"{source.EventId:D}:{source.ReportRevision}:{ReportQualityRules.Version}");
            var activityId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ellai_case_quality_activity
                    (activity_id, case_id, source_event_id, quality_version, role_id, agent_version, policy_version,
                     operation_id, run_id, work_turn_id, source_case_version, input_fingerprint, recorded_at_utc)
                VALUES ({activityId}, {source.CaseId}, {source.EventId}, {ReportQualityRules.Version}, 'ops.quality', '1',
                    {lease.PolicyVersion}, {activityId}, {runId}, {workTurnId}, {candidate.CaseVersion}, {fingerprint}, clock_timestamp())
                """, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        return result == EllaiFencedWorkOutcome.NoChange && concurrentInsert
            ? EllaiFencedWorkOutcome.Retry : result;
    }

    private sealed class QualityCandidate
    {
        public Guid CaseId { get; init; }
        public Guid EventId { get; init; }
        public long CaseVersion { get; init; }
    }
}
