using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using SwiftAid.Infrastructure.Messaging;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;

namespace SwiftAid.Infrastructure.Classification;

/// <summary>Additional lane on the existing ingestion stream, using the same fenced publication semantics.</summary>
public sealed class ClassificationOutboxDispatcher(SwiftAidDbContext db, INatsClient client, TimeProvider clock)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (SELECT "Id" FROM swiftaid_classification.outbox
                WHERE "PublishedAtUtc" IS NULL AND "ExhaustedAtUtc" IS NULL AND "Attempts" >= 8
                  AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc" <= {now})
                ORDER BY "Sequence" LIMIT 100 FOR UPDATE SKIP LOCKED)
            UPDATE swiftaid_classification.outbox o SET "ExhaustedAtUtc" = {now}, "FailureCode" = 'lease-expired',
                "LeaseId" = NULL, "LeaseUntilUtc" = NULL FROM expired WHERE o."Id" = expired."Id"
            """, ct);
        ClassificationEventEntity? row;
        var lease = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            row = await db.Set<ClassificationEventEntity>().FromSqlInterpolated($"""
                SELECT o.* FROM swiftaid_classification.outbox o
                WHERE o."PublishedAtUtc" IS NULL AND o."ExhaustedAtUtc" IS NULL AND o."Attempts" < 8
                  AND o."AvailableAtUtc" <= {now} AND (o."LeaseUntilUtc" IS NULL OR o."LeaseUntilUtc" <= {now})
                  AND NOT EXISTS (SELECT 1 FROM swiftaid_classification.outbox p WHERE p."ResultId" = o."ResultId"
                    AND p."Sequence" < o."Sequence" AND p."PublishedAtUtc" IS NULL)
                ORDER BY o."Sequence" LIMIT 1 FOR UPDATE OF o SKIP LOCKED
                """).SingleOrDefaultAsync(ct);
            if (row is null) return false;
            row.LeaseId = lease; row.LeaseUntilUtc = now.AddSeconds(60); row.Attempts++;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        var sent = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            { schemaVersion = 1, messageId = row.Id, sequence = row.Sequence, resultId = row.ResultId,
                caseId = row.CaseId, reportRevision = row.ReportRevision, eventType = row.EventType, recordedAtUtc = row.CreatedAtUtc });
            var ack = await client.CreateJetStreamContext().PublishAsync("swiftaid.ingestion.v1.classification", bytes,
                opts: new NatsJSPubOpts { ExpectedStream = "SWIFTAID_INGESTION", MsgId = row.Id.ToString("D") }, cancellationToken: timeout.Token);
            ack.EnsureStored();
            sent = ack.Stream == "SWIFTAID_INGESTION" && ack.Seq > 0;
        }
        catch (Exception error) when (error is NatsException or OperationCanceledException) { }
        var completedAt = clock.GetUtcNow();
        var query = db.Set<ClassificationEventEntity>().Where(e => e.Id == row.Id && e.LeaseId == lease && e.LeaseUntilUtc > completedAt);
        if (sent)
            await query.ExecuteUpdateAsync(s => s.SetProperty(e => e.PublishedAtUtc, completedAt).SetProperty(e => e.LeaseId, (Guid?)null)
                .SetProperty(e => e.LeaseUntilUtc, (DateTimeOffset?)null).SetProperty(e => e.FailureCode, (string?)null), ct);
        else
            await query.ExecuteUpdateAsync(s => s.SetProperty(e => e.LeaseId, (Guid?)null).SetProperty(e => e.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(e => e.AvailableAtUtc, completedAt.AddSeconds(Math.Min(300, 1 << row.Attempts)))
                .SetProperty(e => e.ExhaustedAtUtc, row.Attempts >= 8 ? completedAt : (DateTimeOffset?)null)
                .SetProperty(e => e.FailureCode, "publish-unavailable"), ct);
        return true;
    }
}
