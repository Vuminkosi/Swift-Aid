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
            SELECT {job.Id}, e.event_id, row_number() OVER (ORDER BY c.created_at_utc DESC, c.case_id, e.report_revision DESC, e.event_id), 'Pending'
            FROM case_events e JOIN managed_cases c ON c.case_id = e.case_id
            WHERE c.workspace_id = {job.WorkspaceId} AND c.organization_id = {job.OrganizationId}
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
        var source = await (from item in db.Set<ClassificationBackfillItemEntity>().AsNoTracking()
                            join e in db.CaseEvents.AsNoTracking() on item.SourceEventId equals e.EventId
                            where item.JobId == id && item.State == "Pending"
                            orderby item.Ordinal
                            select e).FirstOrDefaultAsync(ct);
        if (source is null) job.State = "Completed";
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

    public async Task BindResultAsync(Guid id, Guid sourceEventId, string configuration, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(id, ct);
        var job = await RequireJobAsync(id, ct);
        if (job.ConfigurationHash != options.Value.Profile.Hash) throw new ArgumentException("Backfill configuration changed.");
        var item = await db.Set<ClassificationBackfillItemEntity>().SingleAsync(i => i.JobId == id && i.SourceEventId == sourceEventId, ct);
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
            job.Eligible, job.Pending, job.NewlyClassified, job.Reused, job.Abstained, job.Failed, job.CreatedAtUtc, job.UpdatedAtUtc));
    }

    private Task<int> LockAsync(Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"classification-backfill:" + id}, 0))", ct);
}
