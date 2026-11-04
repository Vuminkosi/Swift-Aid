using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Infrastructure.Classification;

/// <summary>Creates a bounded classification recovery job for recent, unclassified case revisions.</summary>
public sealed class EllaiEnrichmentWork(SwiftAidDbContext db, IOptions<ClassificationOptions> configured, TimeProvider clock)
{
    public async Task<EllaiFencedWorkOutcome> RunOneAsync(EllaiOperationalLease lease, string organizationId,
        Guid jobId, Guid runId, Guid workTurnId, int batchSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var options = configured.Value;
        if (!options.Enabled || lease.RoleId != "ops.enrichment" || jobId == Guid.Empty || runId == Guid.Empty ||
            workTurnId == Guid.Empty || runId == workTurnId ||
            batchSize is < 1 or > 100 || !options.Organizations.Contains(organizationId, StringComparer.Ordinal))
            throw new InvalidOperationException("The enrichment work scope is unavailable.");
        options.Validate();
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        return await schedule.ExecuteFencedAsync(lease, async (_, _, token) =>
        {
            if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({lease.WorkspaceId}, {organizationId}) AS \"Value\"")
                .SingleAsync(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
            var now = clock.GetUtcNow();
            var profile = JsonSerializer.Serialize(options.Profile);
            var configuration = options.Profile.Hash;
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO swiftaid_classification.backfills
                    ("Id", "WorkerId", "WorkspaceId", "OrganizationId", "ProfileJson", "ConfigurationHash", "State",
                     "Eligible", "Pending", "NewlyClassified", "Reused", "Abstained", "Failed", "Superseded", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({jobId}, {options.WorkerId}, {lease.WorkspaceId}, {organizationId}, {profile}::jsonb, {configuration}, 'Running',
                    0, 0, 0, 0, 0, 0, 0, {now}, {now})
                ON CONFLICT ("Id") DO NOTHING
                """, token).ConfigureAwait(false);
            if (inserted == 0) return false;
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH candidates AS (
                    SELECT c.case_id, e.event_id, report_time.received_at_utc
                    FROM managed_cases c
                    JOIN LATERAL (
                        SELECT event_id FROM case_events
                        WHERE case_id = c.case_id AND report_revision = c.report_revision
                          AND event_type IN ('CaseReported', 'CaseReportCorrected')
                        ORDER BY aggregate_version DESC LIMIT 1
                    ) e ON TRUE
                    CROSS JOIN LATERAL (SELECT COALESCE((
                        SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                        JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                        WHERE promotion.case_id = c.case_id
                    ), c.created_at_utc) AS received_at_utc) report_time
                    WHERE c.workspace_id = {lease.WorkspaceId} AND c.organization_id = {organizationId}
                      AND report_time.received_at_utc BETWEEN {now.AddDays(-30)} AND {now}
                      AND NOT EXISTS (
                        SELECT 1 FROM swiftaid_classification.associations a
                        JOIN swiftaid_classification.results r ON r."Id" = a."ResultId"
                        JOIN swiftaid_classification.configurations cfg ON cfg."Id" = a."ConfigurationId"
                        WHERE a."CaseId" = c.case_id AND a."ReportRevision" = c.report_revision AND r."State" IN ('Completed', 'Failed')
                          AND (cfg."ProfileJson" - 'SourceLanguage' - 'Translation') = ({profile}::jsonb - 'SourceLanguage' - 'Translation')
                          AND (
                            (NOT (cfg."ProfileJson" ? 'Translation') AND NOT EXISTS (
                                SELECT 1 FROM swiftaid_translation.associations ta
                                JOIN swiftaid_translation.artifacts t ON t."Id" = ta."ArtifactId"
                                WHERE ta."CaseId" = c.case_id AND ta."ReportRevision" = c.report_revision
                                  AND t."WorkspaceId" = c.workspace_id AND t."OrganizationId" = c.organization_id
                                  AND t."State" = 'Completed' AND t."ReviewStatus" = 'Approved'
                            )) OR EXISTS (
                                SELECT 1 FROM swiftaid_translation.associations ta
                                JOIN swiftaid_translation.artifacts t ON t."Id" = ta."ArtifactId"
                                WHERE ta."CaseId" = c.case_id AND ta."ReportRevision" = c.report_revision
                                  AND t."WorkspaceId" = c.workspace_id AND t."OrganizationId" = c.organization_id
                                  AND t."State" = 'Completed' AND t."ReviewStatus" = 'Approved'
                                  AND cfg."ProfileJson" -> 'Translation' ->> 'ArtifactId' = t."Id"::text
                                  AND cfg."ProfileJson" -> 'Translation' ->> 'ReviewVersion' = t."ReviewVersion"::text
                            )
                          )
                      )
                      AND NOT EXISTS (
                        SELECT 1 FROM swiftaid_classification.backfill_items item
                        JOIN swiftaid_classification.backfills job ON job."Id" = item."JobId"
                        WHERE item."SourceEventId" = e.event_id AND item."State" = 'Pending' AND job."State" = 'Running'
                          AND job."WorkspaceId" = c.workspace_id AND job."OrganizationId" = c.organization_id
                          AND job."ConfigurationHash" = {configuration}
                      )
                    ORDER BY report_time.received_at_utc DESC, c.case_id LIMIT {batchSize}
                )
                INSERT INTO swiftaid_classification.backfill_items ("JobId", "SourceEventId", "Ordinal", "State")
                SELECT {jobId}, event_id, row_number() OVER (ORDER BY received_at_utc DESC, case_id), 'Pending'
                FROM candidates
                """, token).ConfigureAwait(false);
            if (count == 0) return false;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE swiftaid_classification.backfills SET "Eligible" = {count}, "Pending" = {count} WHERE "Id" = {jobId}
                """, token).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ellai_enrichment_jobs (job_id, run_id, work_turn_id, policy_version)
                VALUES ({jobId}, {runId}, {workTurnId}, {lease.PolicyVersion})
                """, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }
}
