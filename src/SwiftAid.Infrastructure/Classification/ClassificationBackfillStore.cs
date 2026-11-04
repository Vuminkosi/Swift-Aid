using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Events;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;

namespace SwiftAid.Infrastructure.Classification;

/// <summary>Immutable source snapshot and bounded reconciliation; inference uses the existing registry leases.</summary>
public sealed class ClassificationBackfillStore(SwiftAidDbContext db, IOptions<ClassificationOptions> options, TimeProvider clock)
{
    /// <summary>Finds a runnable job only within the configured machine scope.
    /// Job and result locks still arbitrate concurrent workers when it is processed.</summary>
    public async Task<Guid?> FindRunnableAsync(CancellationToken ct)
    {
        options.Value.Validate();
        var configuration = options.Value.Profile.Hash;
        var workerId = options.Value.WorkerId;
        var organizations = options.Value.Organizations;
        // Compare the first pending case in each snapshot, rather than the age of
        // the job itself. Empty jobs remain eligible for completion afterwards.
        return await db.Database.SqlQuery<Guid?>($"""
            SELECT job."Id" AS "Value"
            FROM swiftaid_classification.backfills job
            JOIN workspace_organizations link ON link.workspace_id = job."WorkspaceId" AND link.organization_id = job."OrganizationId" AND link.status = 'Active'
            JOIN workspaces workspace ON workspace.workspace_id = job."WorkspaceId" AND workspace.status = 'Active'
            JOIN organizations organization ON organization.organization_id = job."OrganizationId" AND organization.status = 'Active'
            LEFT JOIN LATERAL (
                SELECT COALESCE((SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                    JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                    WHERE promotion.case_id = c.case_id), c.created_at_utc) AS received_at_utc
                FROM swiftaid_classification.backfill_items item
                JOIN case_events e ON e.event_id = item."SourceEventId"
                JOIN managed_cases c ON c.case_id = e.case_id AND c.workspace_id = job."WorkspaceId" AND c.organization_id = job."OrganizationId"
                WHERE item."JobId" = job."Id" AND item."State" = 'Pending'
                ORDER BY item."Ordinal" LIMIT 1
            ) next_case ON TRUE
            WHERE job."WorkerId" = {workerId} AND job."State" = 'Running' AND job."ConfigurationHash" = {configuration}
                AND job."OrganizationId" = ANY ({organizations})
            ORDER BY next_case.received_at_utc DESC NULLS LAST, job."UpdatedAtUtc", job."Id"
            LIMIT 1
            """).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<ClassificationBackfillStatus> StartAsync(StartClassificationBackfill request, CancellationToken ct)
    {
        options.Value.Validate();
        if (request.JobId == Guid.Empty) throw new ArgumentException("A stable job identity is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(request.JobId, ct);
        await RequireScopeAsync(request.WorkspaceId, request.OrganizationId, ct);
        var job = await db.Set<ClassificationBackfillEntity>().FindAsync([request.JobId], ct);
        if (job is not null)
        {
            if (job.WorkerId != options.Value.WorkerId || job.WorkspaceId != request.WorkspaceId || job.OrganizationId != request.OrganizationId)
                throw new UnauthorizedAccessException();
            return await SummarizeAsync(job, ct);
        }
        var now = clock.GetUtcNow();
        job = new ClassificationBackfillEntity
        {
            Id = request.JobId, WorkerId = options.Value.WorkerId, WorkspaceId = request.WorkspaceId,
            OrganizationId = request.OrganizationId, ConfigurationHash = options.Value.Profile.Hash,
            ProfileJson = JsonSerializer.Serialize(options.Value.Profile), State = "Running", CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.Add(job);
        await db.SaveChangesAsync(ct);
        // One SQL snapshot sees only committed source facts. No paging through a moving live dataset.
        job.Eligible = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO swiftaid_classification.backfill_items ("JobId", "SourceEventId", "Ordinal", "State")
            SELECT {job.Id}, e.event_id, row_number() OVER (ORDER BY report_time.received_at_utc DESC, c.case_id, e.report_revision DESC, e.event_id), 'Pending'
            FROM case_events e JOIN managed_cases c ON c.case_id = e.case_id
            CROSS JOIN LATERAL (SELECT COALESCE((
                SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                WHERE promotion.case_id = c.case_id
            ), c.created_at_utc) AS received_at_utc) report_time
            WHERE c.workspace_id = {job.WorkspaceId} AND c.organization_id = {job.OrganizationId}
              AND report_time.received_at_utc BETWEEN {now.AddDays(-30)} AND {now}
              AND e.event_type IN ('CaseReported', 'CaseReportCorrected')
            """, ct);
        job.Pending = job.Eligible;
        await db.SaveChangesAsync(ct);
        var status = await SummarizeAsync(job, ct);
        await transaction.CommitAsync(ct);
        return status;
    }

    public async Task<ClassificationBackfillStatus> StatusAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await RequireJobAsync(id, ct);
        return await SummarizeAsync(job, ct);
    }

    public async Task<ClassificationBackfillStatus> ControlAsync(Guid id, bool paused, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(id, ct);
        var job = await RequireJobAsync(id, ct);
        if (job.State != "Completed") job.State = paused ? "Paused" : "Running";
        job.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var status = await SummarizeAsync(job, ct);
        await transaction.CommitAsync(ct);
        return status;
    }

    public async Task<ClassificationBackfillWork> NextAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(id, ct);
        var job = await RequireJobAsync(id, ct);
        if (job.State != "Running" || job.ConfigurationHash != options.Value.Profile.Hash)
            return new(await SummarizeAsync(job, ct), null);
        // ResultId is bound only after the registry resolves the exact source-language configuration.
        // Limit each reconciliation transaction; independent jobs and live consumers are not serialized.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH pending AS (
                SELECT * FROM swiftaid_classification.backfill_items
                WHERE "JobId" = {id} AND "State" = 'Pending' ORDER BY "Ordinal" LIMIT 100
            ), finished AS (
                SELECT i."SourceEventId", r."Id" AS result_id,
                    CASE WHEN r."State" = 'Failed' THEN 'Failed'
                         WHEN r."AbstentionReason" IS NOT NULL THEN 'Abstained'
                         WHEN a."Reused" OR r."CreatedAtUtc" < {job.CreatedAtUtc} THEN 'Reused' ELSE 'NewlyClassified' END AS outcome
                FROM pending i
                JOIN swiftaid_classification.results r ON r."Id" = i."ResultId"
                JOIN swiftaid_classification.associations a ON a."SourceEventId" = i."SourceEventId" AND a."ResultId" = r."Id"
                WHERE i."JobId" = {id} AND i."State" = 'Pending' AND r."State" IN ('Completed','Failed')
                  AND r."WorkspaceId" = {job.WorkspaceId} AND r."OrganizationId" = {job.OrganizationId}
            ), applied AS (
                UPDATE swiftaid_classification.backfill_items i SET "ResultId" = f.result_id, "State" = f.outcome
                FROM finished f WHERE i."JobId" = {id} AND i."SourceEventId" = f."SourceEventId"
                RETURNING i."State"
            ), counts AS (
                SELECT count(*) AS total,
                    count(*) FILTER (WHERE "State" = 'NewlyClassified') AS classified,
                    count(*) FILTER (WHERE "State" = 'Reused') AS reused,
                    count(*) FILTER (WHERE "State" = 'Abstained') AS abstained,
                    count(*) FILTER (WHERE "State" = 'Failed') AS failed FROM applied
            )
            UPDATE swiftaid_classification.backfills j SET "Pending" = j."Pending" - c.total,
                "NewlyClassified" = j."NewlyClassified" + c.classified, "Reused" = j."Reused" + c.reused,
                "Abstained" = j."Abstained" + c.abstained, "Failed" = j."Failed" + c.failed
            FROM counts c WHERE j."Id" = {id}
            """, ct);
        await db.Entry(job).ReloadAsync(ct);
        var expiredCandidates = await db.Database.SqlQuery<ExpiryCandidate>($"""
            SELECT item."SourceEventId" AS "SourceEventId", item."ResultId" AS "ResultId"
            FROM swiftaid_classification.backfill_items item
            JOIN case_events e ON e.event_id = item."SourceEventId"
            JOIN managed_cases c ON c.case_id = e.case_id
            WHERE item."JobId" = {id} AND item."State" = 'Pending'
                AND NOT (COALESCE((SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                    JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                    WHERE promotion.case_id = c.case_id), c.created_at_utc)
                    BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp())
            ORDER BY item."Ordinal" LIMIT 100
            """).ToArrayAsync(ct);
        foreach (var candidate in expiredCandidates)
        {
            if (candidate.ResultId is { } resultId)
            {
                var key = $"classification-result:{resultId}";
                if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({key}, 0)) AS \"Value\"").SingleAsync(ct))
                    continue;
                var result = await db.Set<ClassificationResultEntity>().AsNoTracking().SingleAsync(row => row.Id == resultId, ct);
                // Terminal outcomes reconcile on the next pass; active leases may complete.
                if (result.State is "Completed" or "Failed" || result.State == "Leased" && result.LeaseUntilUtc > clock.GetUtcNow())
                    continue;
            }
            var item = await db.Set<ClassificationBackfillItemEntity>().SingleAsync(row => row.JobId == id && row.SourceEventId == candidate.SourceEventId, ct);
            if (item.State != "Pending") continue;
            item.State = "Expired";
            job.Pending--;
            job.Expired++;
        }
        await db.SaveChangesAsync(ct);
        var sourceId = await db.Database.SqlQuery<Guid?>($"""
            SELECT e.event_id AS "Value" FROM swiftaid_classification.backfill_items item
            JOIN case_events e ON e.event_id = item."SourceEventId"
            JOIN managed_cases c ON c.case_id = e.case_id
            WHERE item."JobId" = {id} AND item."State" = 'Pending'
                AND COALESCE((SELECT report.received_at_utc FROM swiftaid_intake.intake_promotions promotion
                    JOIN swiftaid_intake.intake_reports report ON report.receipt_id = promotion.receipt_id
                    WHERE promotion.case_id = c.case_id), c.created_at_utc)
                    BETWEEN clock_timestamp() - interval '30 days' AND clock_timestamp()
            ORDER BY item."Ordinal" LIMIT 1
            """).FirstOrDefaultAsync(ct);
        var source = sourceId is null ? null : await db.CaseEvents.AsNoTracking().SingleAsync(e => e.EventId == sourceId, ct);
        if (source is null && job.Pending == 0) job.State = "Completed";
        job.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var status = await SummarizeAsync(job, ct);
        await transaction.CommitAsync(ct);
        return new(status, source is null ? null : new IngestionNotice(1, source.EventId, "case", source.CaseId,
            source.AggregateVersion, source.ReportRevision, source.EventType, source.RecordedAtUtc));
    }

    public async Task ValidateNoticeAsync(Guid id, Guid sourceEventId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await RequireJobAsync(id, ct);
        if (job.ConfigurationHash != options.Value.Profile.Hash) throw new ArgumentException("Backfill configuration changed.");
        if (!await db.Set<ClassificationBackfillItemEntity>().AnyAsync(i => i.JobId == id && i.SourceEventId == sourceEventId, ct))
            throw new UnauthorizedAccessException();
    }

    public async Task BindResultAsync(Guid id, Guid sourceEventId, string configuration, CancellationToken ct, string? claimState = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(id, ct);
        var job = await RequireJobAsync(id, ct);
        if (job.ConfigurationHash != options.Value.Profile.Hash) throw new ArgumentException("Backfill configuration changed.");
        var item = await db.Set<ClassificationBackfillItemEntity>().SingleAsync(i => i.JobId == id && i.SourceEventId == sourceEventId, ct);
        if (claimState == "Superseded")
        {
            // Confirm the durable inbox and authoritative current revision under
            // the job lock; a worker-supplied status alone grants no authority.
            var obsolete = await (from source in db.CaseEvents.AsNoTracking()
                join current in db.ManagedCases.AsNoTracking() on source.CaseId equals current.CaseId
                join inbox in db.Set<ClassificationInboxEntity>().AsNoTracking() on source.EventId equals inbox.MessageId
                where source.EventId == sourceEventId && current.WorkspaceId == job.WorkspaceId &&
                    current.OrganizationId == job.OrganizationId && source.ReportRevision < current.ReportRevision &&
                    inbox.ConfigurationId == configuration && inbox.State == "Superseded"
                select source.EventId).AnyAsync(ct);
            if (!obsolete) throw new InvalidOperationException("The superseded result is not current.");
            if (item.State == "Superseded") return;
            if (item.State != "Pending") throw new InvalidOperationException("The backfill item is already finalized.");
            item.State = "Superseded";
            job.Pending--;
            job.Superseded++;
            job.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
        var resultId = await db.Set<ClassificationAssociationEntity>().Where(a => a.SourceEventId == sourceEventId && a.ConfigurationId == configuration)
            .Select(a => a.ResultId).SingleAsync(ct);
        if (item.ResultId is not null && item.ResultId != resultId) throw new InvalidOperationException("Bound backfill artifact changed.");
        item.ResultId = resultId;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<ClassificationBackfillEntity> RequireJobAsync(Guid id, CancellationToken ct)
    {
        var job = await db.Set<ClassificationBackfillEntity>().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || job.WorkerId != options.Value.WorkerId) throw new UnauthorizedAccessException();
        await db.Entry(job).ReloadAsync(ct);
        await RequireScopeAsync(job.WorkspaceId, job.OrganizationId, ct);
        return job;
    }

    private async Task RequireScopeAsync(string workspace, string organization, CancellationToken ct)
    {
        if (!options.Value.Organizations.Contains(organization, StringComparer.Ordinal) ||
            !await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({workspace}, {organization}) AS \"Value\"").SingleAsync(ct))
            throw new UnauthorizedAccessException();
    }

    private Task<ClassificationBackfillStatus> SummarizeAsync(ClassificationBackfillEntity job, CancellationToken ct)
    {
        return Task.FromResult(new ClassificationBackfillStatus(job.Id,
            job.ConfigurationHash == options.Value.Profile.Hash ? job.State : "ConfigurationChanged", job.ConfigurationHash,
            job.Eligible, job.Pending, job.NewlyClassified, job.Reused, job.Abstained, job.Failed, job.CreatedAtUtc, job.UpdatedAtUtc, job.Superseded, job.Expired));
    }

    private Task<int> LockAsync(Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"classification-backfill:" + id}, 0))", ct);

    private sealed class ExpiryCandidate
    {
        public Guid SourceEventId { get; init; }
        public Guid? ResultId { get; init; }
    }
}
