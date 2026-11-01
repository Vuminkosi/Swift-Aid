using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Messaging;
using SwiftAid.Infrastructure.Persistence.Surveillance;

namespace SwiftAid.Infrastructure.Surveillance;

public sealed class SurveillanceOutboxDispatcher(SwiftAidDbContext db, INatsClient client,
    IOptions<SurveillanceOptions> options, TimeProvider clock)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return false;
        options.Value.Validate();
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var item = await db.Set<SurveillanceEventEntity>().FromSqlInterpolated($"""
            SELECT o.* FROM swiftaid_surveillance.outbox o
            JOIN swiftaid_surveillance.signals s ON s."Id" = o."SignalId"
            WHERE o."PublishedAtUtc" IS NULL AND o."Attempts" < 5 AND o."AvailableAtUtc" <= {now}
                AND s."WorkspaceId" = ANY({options.Value.Workspaces}) AND s."OrganizationId" = ANY({options.Value.Organizations})
                AND NOT EXISTS (SELECT 1 FROM swiftaid_surveillance.outbox p WHERE p."SignalId" = o."SignalId"
                    AND p."Version" < o."Version" AND p."PublishedAtUtc" IS NULL)
            ORDER BY o."RecordedAtUtc", o."Id" LIMIT 1 FOR UPDATE OF o SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (item is null) return false;
        item.Attempts++;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, messageId = item.Id,
                signalId = item.SignalId, version = item.Version, eventType = item.EventType, recordedAtUtc = item.RecordedAtUtc });
            var ack = await client.CreateJetStreamContext().PublishAsync("swiftaid.ingestion.v1.surveillance", bytes,
                opts: new NatsJSPubOpts { ExpectedStream = "SWIFTAID_INGESTION", MsgId = item.Id.ToString("D") }, cancellationToken: timeout.Token);
            ack.EnsureStored();
            if (ack.Stream != "SWIFTAID_INGESTION" || ack.Seq == 0) throw new InvalidOperationException("Missing durable publication acknowledgement.");
            item.PublishedAtUtc = clock.GetUtcNow();
            item.FailureCode = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            item.FailureCode = "publication-unavailable";
            item.AvailableAtUtc = now.AddSeconds(30);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
