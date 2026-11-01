using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using SwiftAid.Contracts.Events;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Messaging;
using SwiftAid.Infrastructure.Persistence.Classification;

namespace SwiftAid.Infrastructure.Classification;

public sealed class TranslationOutboxDispatcher(SwiftAidDbContext db, INatsClient client, TimeProvider clock,
    TranslationRegistry registry, IOptions<TranslationOptions> options)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        var organizations = options.Value.Organizations.Where(registry.Allows).ToArray();
        if (organizations.Length == 0) return false;
        var now = clock.GetUtcNow();
        await db.Set<TranslationOutboxEntity>().Where(x => x.PublishedAtUtc == null && x.Attempts >= 8 &&
            (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now) && x.FailureCode == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailureCode, "lease-expired").SetProperty(x => x.LeaseId, (Guid?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTimeOffset?)null), ct);
        TranslationOutboxEntity? row;
        IngestionNotice notice;
        var lease = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            row = await db.Set<TranslationOutboxEntity>().FromSqlInterpolated($"""
                SELECT o.* FROM swiftaid_translation.outbox o WHERE o."PublishedAtUtc" IS NULL AND o."Attempts" < 8
                AND o."AvailableAtUtc" <= {now} AND (o."LeaseUntilUtc" IS NULL OR o."LeaseUntilUtc" <= {now})
                AND EXISTS (SELECT 1 FROM swiftaid_translation.artifacts a WHERE a."Id" = o."ArtifactId" AND a."OrganizationId" = ANY({organizations}))
                ORDER BY o."AvailableAtUtc" LIMIT 1 FOR UPDATE OF o SKIP LOCKED
                """).SingleOrDefaultAsync(ct);
            if (row is null) return false;
            var source = await db.CaseEvents.AsNoTracking().SingleAsync(x => x.EventId == row.SourceEventId, ct);
            notice = new(1, source.EventId, "case", source.CaseId, source.AggregateVersion, source.ReportRevision, source.EventType, source.RecordedAtUtc);
            row.LeaseId = lease; row.LeaseUntilUtc = now.AddSeconds(60); row.Attempts++;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        var sent = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var ack = await client.CreateJetStreamContext().PublishAsync("swiftaid.ingestion.v1.case",
                JsonSerializer.SerializeToUtf8Bytes(notice, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                opts: new NatsJSPubOpts { ExpectedStream = "SWIFTAID_INGESTION", MsgId = row.Id.ToString("D") }, cancellationToken: timeout.Token);
            ack.EnsureStored(); sent = ack.Stream == "SWIFTAID_INGESTION" && ack.Seq > 0;
        }
        catch (Exception error) when (error is NatsException or OperationCanceledException) { }
        var completed = clock.GetUtcNow();
        var owned = db.Set<TranslationOutboxEntity>().Where(x => x.Id == row.Id && x.LeaseId == lease && x.LeaseUntilUtc > completed);
        await owned.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, (Guid?)null)
            .SetProperty(x => x.LeaseUntilUtc, (DateTimeOffset?)null)
            .SetProperty(x => x.PublishedAtUtc, sent ? completed : (DateTimeOffset?)null)
            .SetProperty(x => x.AvailableAtUtc, completed.AddSeconds(Math.Min(300, 1 << row.Attempts)))
            .SetProperty(x => x.FailureCode, sent ? null : "publication-unavailable"), ct);
        return true;
    }
}
