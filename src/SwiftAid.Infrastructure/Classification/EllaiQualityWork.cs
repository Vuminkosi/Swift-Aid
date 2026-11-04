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
        return await schedule.ExecuteFencedAsync(lease, async (_, _, token) =>
        {
            // The source case stays locked while its current facts revision is assessed.
            // Previously assessed revisions are excluded before unprotecting evidence.
            var candidates = await db.Database.SqlQuery<QualityCandidate>($"""
                SELECT c.case_id AS "CaseId", e.event_id AS "EventId", c.aggregate_version AS "CaseVersion"
                FROM managed_cases c
                JOIN LATERAL (
                    SELECT event_id FROM case_events
                    WHERE case_id = c.case_id AND report_revision = c.report_revision
                        AND event_type IN ('CaseReported', 'CaseReportCorrected')
                    ORDER BY aggregate_version DESC LIMIT 1
                ) e ON TRUE
                WHERE c.workspace_id = {lease.WorkspaceId} AND c.organization_id = ANY({options.Organizations})
                    AND NOT EXISTS (
                        SELECT 1 FROM swiftaid_classification.quality_assessments q
                        WHERE q."SourceEventId" = e.event_id AND q."Version" = {ReportQualityRules.Version})
                ORDER BY c.case_id FOR SHARE OF c SKIP LOCKED LIMIT 1
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
            if (inserted == 0) return false;
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
    }

    private sealed class QualityCandidate
    {
        public Guid CaseId { get; init; }
        public Guid EventId { get; init; }
        public long CaseVersion { get; init; }
    }
}
