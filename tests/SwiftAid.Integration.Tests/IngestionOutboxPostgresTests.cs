using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Net;
using SwiftAid.Application.Abstractions;
using SwiftAid.Contracts.Events;
using SwiftAid.Contracts.Intake;
using SwiftAid.Infrastructure.Messaging;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Processing;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class IngestionOutboxPostgresTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    [IntakePostgresFact]
    public async Task Concurrent_publishers_claim_disjoint_rows()
    {
        await using var db = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 12; i++) await SeedIntakeAsync(db, now);
        await using var other = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var results = await Task.WhenAll(
            new PostgresIngestionOutbox(db).ClaimAsync("intake", now, LeaseDuration, 6, Ct).AsTask(),
            new PostgresIngestionOutbox(other).ClaimAsync("intake", now, LeaseDuration, 6, Ct).AsTask());
        Assert.All(results, rows => Assert.Equal(6, rows.Count));
        Assert.Equal(12, results.SelectMany(rows => rows).Select(row => row.OutboxId).Distinct().Count());
    }

    [IntakePostgresFact]
    public async Task Expired_owner_cannot_complete_or_fail_a_reclaimed_delivery()
    {
        await using var db = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedIntakeAsync(db, now);
        var store = new PostgresIngestionOutbox(db);
        var first = Assert.Single(await store.ClaimAsync("intake", now, LeaseDuration, 1, Ct));
        Assert.False(await store.CompleteAsync(first, now.AddSeconds(31), Ct));
        var second = Assert.Single(await store.ClaimAsync("intake", now.AddSeconds(31), LeaseDuration, 1, Ct));
        Assert.Equal(first.Notice.MessageId, second.Notice.MessageId);
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.False(await store.CompleteAsync(first, now.AddSeconds(32), Ct));
        Assert.False(await store.FailAsync(first, now.AddSeconds(32), "publish-failed", Ct));
        Assert.True(await store.CompleteAsync(second, now.AddSeconds(32), Ct));
        Assert.False(await store.CompleteAsync(second, now.AddSeconds(33), Ct));
    }

    [IntakePostgresFact]
    public async Task Retries_are_bounded_and_an_expired_final_attempt_is_observable()
    {
        await using var db = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        var id = await SeedIntakeAsync(db, now);
        var store = new PostgresIngestionOutbox(db);
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var lease = Assert.Single(await store.ClaimAsync("intake", now, LeaseDuration, 1, Ct));
            Assert.Equal(attempt, lease.Attempt);
            if (attempt < 8)
            {
                Assert.True(await store.FailAsync(lease, now, "jetstream-unavailable", Ct));
                Assert.Empty(await store.ClaimAsync("intake", now, LeaseDuration, 1, Ct));
            }
            now = now.AddMinutes(10);
        }
        Assert.Empty(await store.ClaimAsync("intake", now, LeaseDuration, 1, Ct));
        var row = await db.IntakeOutbox.AsNoTracking().SingleAsync(row => row.OutboxId == id);
        Assert.Equal(8, row.Attempts);
        Assert.NotNull(row.ExhaustedAtUtc);
        Assert.Null(row.PublishedAtUtc);
        Assert.Null(row.LeaseId);
        Assert.Equal("lease-expired", row.LastFailureCode);
    }

    [IntakePostgresFact]
    public async Task Case_revisions_wait_for_previous_publication_and_exhaustion_does_not_skip_them()
    {
        await using var db = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedCaseAsync(db, now);
        var store = new PostgresIngestionOutbox(db);
        var first = Assert.Single(await store.ClaimAsync("case", now, LeaseDuration, 10, Ct));
        Assert.Equal(1, first.Notice.SourceVersion);
        Assert.Empty(await store.ClaimAsync("case", now, LeaseDuration, 10, Ct));
        await db.DomainOutbox.Where(row => row.OutboxId == first.OutboxId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AttemptCount, 8));
        Assert.True(await store.FailAsync(first, now, "publish-failed", Ct));
        Assert.Empty(await store.ClaimAsync("case", now.AddHours(1), LeaseDuration, 10, Ct));
        // Deliberate operator recovery of this exact failed row, never a new event identity.
        await db.DomainOutbox.Where(row => row.OutboxId == first.OutboxId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExhaustedAtUtc, (DateTimeOffset?)null)
                .SetProperty(row => row.AttemptCount, 0).SetProperty(row => row.AvailableAtUtc, now));
        var replay = Assert.Single(await store.ClaimAsync("case", now, LeaseDuration, 10, Ct));
        Assert.Equal(first.Notice.MessageId, replay.Notice.MessageId);
        Assert.True(await store.CompleteAsync(replay, now, Ct));
        var second = Assert.Single(await store.ClaimAsync("case", now, LeaseDuration, 10, Ct));
        Assert.Equal(2, second.Notice.SourceVersion);
        Assert.True(await store.CompleteAsync(second, now, Ct));
    }

    [IntakePostgresFact]
    public async Task Provider_failure_is_not_acknowledged_as_published()
    {
        await using var db = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        var id = await SeedIntakeAsync(db, now);
        var service = new IngestionDispatchService(new PostgresIngestionOutbox(db), new FailedPublisher(), TimeProvider.System);
        Assert.True(await service.DispatchOneAsync("intake", Ct));
        var row = await db.IntakeOutbox.AsNoTracking().SingleAsync(row => row.OutboxId == id);
        Assert.Null(row.PublishedAtUtc);
        Assert.Null(row.LeaseId);
        Assert.Equal("jetstream-unavailable", row.LastFailureCode);
    }

    [IntakePostgresFact]
    public async Task Real_jetstream_ack_then_database_crash_replays_the_same_message()
    {
        // This test requires its own unauthenticated loopback broker, never a shared deployment.
        var url = Environment.GetEnvironmentVariable("SWIFTAID_TEST_INGESTION_NATS_URL");
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == "127.0.0.1" && uri.Port == 44229,
            "Set SWIFTAID_TEST_INGESTION_NATS_URL=nats://127.0.0.1:44229 for the disposable fixture.");
        await using var db = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedIntakeAsync(db, now);
        await using var client = new NatsClient(new NatsOpts { Url = url!, RequestTimeout = TimeSpan.FromSeconds(3) });
        var js = client.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig
        {
            Name = IngestionNotice.StreamName, Subjects = [$"{IngestionNotice.SubjectPrefix}.>"],
            MaxMsgs = 10000, MaxBytes = 16 * 1024 * 1024, MaxMsgSize = 4096, DuplicateWindow = TimeSpan.FromMinutes(2)
        });
        var store = new PostgresIngestionOutbox(db);
        var publisher = new JetStreamIngestionNoticePublisher(client);
        var first = Assert.Single(await store.ClaimAsync("intake", now, LeaseDuration, 1, Ct));
        var ack = await publisher.PublishAsync(first.Notice, Ct);
        Assert.Equal(EventPublishStatus.AcknowledgedByJetStream, ack.Status);
        // Simulate process loss immediately after PubAck: deliberately omit CompleteAsync.
        var replay = Assert.Single(await store.ClaimAsync("intake", now.AddSeconds(31), LeaseDuration, 1, Ct));
        var duplicateAck = await publisher.PublishAsync(replay.Notice, Ct);
        Assert.Equal(ack.StreamSequence, duplicateAck.StreamSequence);
        Assert.True(await store.CompleteAsync(replay, now.AddSeconds(31), Ct));
        Assert.Empty(await store.ClaimAsync("intake", now.AddMinutes(2), LeaseDuration, 1, Ct));
    }

    private static async Task<SwiftAidDbContext> FreshAsync()
    {
        var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        // The shared fixture rejects non-loopback and non-test database names. This collection is serial.
        await db.IntakeOutbox.ExecuteDeleteAsync();
        await db.DomainOutbox.ExecuteDeleteAsync();
        return db;
    }

    internal static async Task<Guid> SeedIntakeAsync(SwiftAidDbContext db, DateTimeOffset now)
    {
        var session = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        var outbox = Guid.NewGuid();
        db.IntakeSessions.Add(new IntakeSessionEntity { SessionId = session, ReceiptProofDigest = new byte[32], CreatedAtUtc = now, LastSeenAtUtc = now });
        db.IntakeReports.Add(new IntakeReportEntity
        {
            ReceiptId = receipt, SessionId = session, ClientSubmissionId = Guid.NewGuid(), CanonicalPayloadDigest = new byte[32],
            ProtectedPayload = [1], Status = IntakeStatus.ReceivedUnverified, RoutingStatus = IntakeRoutingStatus.NeedsRouting,
            Version = 1, ReceivedAtUtc = now
        });
        db.IntakeOutbox.Add(new IntakeOutboxEntity
        {
            OutboxId = outbox, ReceiptId = receipt, Revision = 1, EventType = "IntakeReceived", CreatedAtUtc = now, AvailableAtUtc = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return outbox;
    }

    private static async Task SeedCaseAsync(SwiftAidDbContext db, DateTimeOffset now)
    {
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var commandId = Guid.NewGuid();
        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(), ActorId = "officer-1", CommandId = commandId, CaseId = caseId,
            CommandType = "RegisterCase", SchemaVersion = 1, CanonicalPayloadHash = new byte[32],
            FirstAggregateVersion = 1, LastAggregateVersion = 2, ReportRevision = 1, AcceptedAtUtc = now
        });
        for (var version = 1; version <= 2; version++)
        {
            var payload = Guid.NewGuid();
            var eventId = Guid.NewGuid();
            db.Set<ProtectedEventPayloadEntity>().Add(new ProtectedEventPayloadEntity
            { PayloadId = payload, ProtectedBytes = [1], ProtectionPurposeVersion = "synthetic-test/1", CreatedAtUtc = now });
            db.CaseEvents.Add(new CaseEventEntity
            {
                EventId = eventId, CaseId = caseId, AggregateVersion = version, ReportRevision = 1, CommandId = commandId,
                ActorId = "officer-1", EventType = version == 1 ? "CaseReported" : "CaseAssigned", SchemaVersion = 1,
                RecordedAtUtc = now, PayloadId = payload
            });
            db.DomainOutbox.Add(new DomainOutboxEntity
            { OutboxId = Guid.NewGuid(), EventId = eventId, MessageType = "case-event", CreatedAtUtc = now, AvailableAtUtc = now });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class FailedPublisher : IIngestionNoticePublisher
    {
        public ValueTask<EventPublishResult> PublishAsync(IngestionNotice notice, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EventPublishResult(EventPublishStatus.Unavailable, FailureCode: "jetstream-unavailable"));
    }
}
