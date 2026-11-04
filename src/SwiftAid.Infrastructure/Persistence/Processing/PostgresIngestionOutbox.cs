using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Application.Abstractions;
using SwiftAid.Contracts.Events;

namespace SwiftAid.Infrastructure.Persistence.Processing;

// SQL identifiers cannot be parameters. Every interpolated identifier below comes from Shape.For's
// two literal mappings; all runtime values use NpgsqlParameter. No caller-supplied SQL is accepted.
#pragma warning disable EF1002
/// <summary>Fenced delivery from the original transactional outboxes. No source payload is loaded.</summary>
public sealed class PostgresIngestionOutbox(SwiftAidDbContext db) : IIngestionOutbox
{
    public const int MaximumAttempts = 8;

    public async ValueTask<IReadOnlyList<IngestionDeliveryLease>> ClaimAsync(
        string sourceKind, DateTimeOffset now, TimeSpan leaseDuration, int batchSize, CancellationToken cancellationToken)
    {
        var shape = Shape.For(sourceKind);
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (leaseDuration < TimeSpan.FromSeconds(10) || leaseDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        now = now.ToUniversalTime();

        // A process may die on its last allowed attempt. Expiry must still produce a durable terminal state.
        await db.Database.ExecuteSqlRawAsync($"""
            WITH expired AS (
                SELECT outbox_id FROM {shape.Table}
                WHERE published_at_utc IS NULL AND exhausted_at_utc IS NULL
                  AND {shape.Attempts} >= {MaximumAttempts}
                  AND (lease_until_utc IS NULL OR lease_until_utc <= @now)
                ORDER BY available_at_utc, created_at_utc LIMIT 100 FOR UPDATE SKIP LOCKED
            )
            UPDATE {shape.Table} o
            SET exhausted_at_utc = @now, lease_id = NULL, lease_until_utc = NULL,
                last_failure_code = 'lease-expired'
            FROM expired WHERE o.outbox_id = expired.outbox_id
            """, [new NpgsqlParameter("now", now)], cancellationToken).ConfigureAwait(false);

        // Identifier fragments come only from the closed Shape mapping, never request/model data.
        // A failed older revision deliberately blocks later revisions until an operator requeues it.
        var rows = await db.Database.SqlQueryRaw<LeaseRow>($"""
            WITH due AS (
                SELECT o.outbox_id FROM {shape.Table} o {shape.Join}
                WHERE o.published_at_utc IS NULL AND o.exhausted_at_utc IS NULL
                  AND o.available_at_utc <= @now
                  AND (o.lease_until_utc IS NULL OR o.lease_until_utc <= @now)
                  AND o.{shape.Attempts} < {MaximumAttempts}
                  AND NOT EXISTS ({shape.Predecessor})
                ORDER BY o.available_at_utc, o.created_at_utc, o.outbox_id
                LIMIT @batch FOR UPDATE OF o SKIP LOCKED
            ), claimed AS (
                UPDATE {shape.Table} o SET {shape.Attempts} = o.{shape.Attempts} + 1,
                    lease_id = gen_random_uuid(), lease_until_utc = @until
                FROM due WHERE o.outbox_id = due.outbox_id RETURNING o.*
            )
            SELECT o.outbox_id AS "OutboxId", o.lease_id AS "LeaseId",
                o.{shape.Attempts} AS "Attempt", o.lease_until_utc AS "LeaseUntilUtc",
                {shape.MessageId} AS "MessageId", {shape.SourceId} AS "SourceId",
                {shape.Version} AS "SourceVersion", {shape.Revision} AS "ReportRevision",
                {shape.EventType} AS "EventType", o.created_at_utc AS "RecordedAtUtc"
            FROM claimed o {shape.Join}
            """, new NpgsqlParameter("now", now), new NpgsqlParameter("until", now + leaseDuration),
            new NpgsqlParameter("batch", batchSize)).ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(row => new IngestionDeliveryLease(row.OutboxId, row.LeaseId, row.Attempt,
            row.LeaseUntilUtc, new IngestionNotice(1, row.MessageId, sourceKind, row.SourceId,
                row.SourceVersion, row.ReportRevision, row.EventType, row.RecordedAtUtc))).ToArray();
    }

    public async ValueTask<bool> CompleteAsync(
        IngestionDeliveryLease lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var shape = Shape.For(lease.Notice.SourceKind);
        var changed = await db.Database.ExecuteSqlRawAsync($"""
            UPDATE {shape.Table} SET published_at_utc = @now, lease_id = NULL,
                lease_until_utc = NULL, last_failure_code = NULL
            WHERE outbox_id = @id AND lease_id = @lease AND lease_until_utc > @now
              AND published_at_utc IS NULL AND exhausted_at_utc IS NULL
            """, Parameters(lease, now), cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    public async ValueTask<bool> FailAsync(
        IngestionDeliveryLease lease, DateTimeOffset now, string failureCode, CancellationToken cancellationToken)
    {
        if (failureCode is not ("publish-timeout" or "jetstream-unavailable" or "invalid-notice" or
            "publish-failed" or "unexpected-stream")) throw new ArgumentException("Unknown failure code.", nameof(failureCode));
        var shape = Shape.For(lease.Notice.SourceKind);
        var parameters = Parameters(lease, now).Concat(new[]
        {
            new NpgsqlParameter("failure", failureCode),
            new NpgsqlParameter("available", now.ToUniversalTime().AddSeconds(Math.Min(300, 1 << Math.Min(lease.Attempt, 8))))
        }).ToArray();
        var changed = await db.Database.ExecuteSqlRawAsync($"""
            UPDATE {shape.Table} SET lease_id = NULL, lease_until_utc = NULL,
                last_failure_code = @failure, available_at_utc = @available,
                exhausted_at_utc = CASE WHEN {shape.Attempts} >= {MaximumAttempts} THEN @now ELSE NULL END
            WHERE outbox_id = @id AND lease_id = @lease AND lease_until_utc > @now
              AND published_at_utc IS NULL AND exhausted_at_utc IS NULL
            """, parameters, cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    private static NpgsqlParameter[] Parameters(IngestionDeliveryLease lease, DateTimeOffset now) =>
        [new("id", lease.OutboxId), new("lease", lease.LeaseId), new("now", now.ToUniversalTime())];

    private sealed record Shape(string Table, string Attempts, string Join, string Predecessor,
        string MessageId, string SourceId, string Version, string Revision, string EventType)
    {
        public static Shape For(string kind) => kind switch
        {
            "intake" => new("swiftaid_intake.intake_outbox", "attempts", "",
                "SELECT 1 FROM swiftaid_intake.intake_outbox p WHERE p.receipt_id = o.receipt_id AND p.revision < o.revision AND p.published_at_utc IS NULL",
                "o.outbox_id", "o.receipt_id", "o.revision", "o.revision", "o.event_type"),
            "case" => new("public.domain_outbox", "attempt_count", "JOIN public.case_events e ON e.event_id = o.event_id",
                "SELECT 1 FROM public.domain_outbox p JOIN public.case_events pe ON pe.event_id = p.event_id WHERE pe.case_id = e.case_id AND pe.aggregate_version < e.aggregate_version AND p.published_at_utc IS NULL",
                "o.event_id", "e.case_id", "e.aggregate_version", "e.report_revision", "e.event_type"),
            _ => throw new ArgumentException("Unknown ingestion source kind.", nameof(kind))
        };
    }

    private sealed class LeaseRow
    {
        public Guid OutboxId { get; set; }
        public Guid LeaseId { get; set; }
        public int Attempt { get; set; }
        public DateTimeOffset LeaseUntilUtc { get; set; }
        public Guid MessageId { get; set; }
        public Guid SourceId { get; set; }
        public long SourceVersion { get; set; }
        public long ReportRevision { get; set; }
        public required string EventType { get; set; }
        public DateTimeOffset RecordedAtUtc { get; set; }
    }
}
#pragma warning restore EF1002
