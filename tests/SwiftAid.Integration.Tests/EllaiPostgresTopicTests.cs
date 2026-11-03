using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

/// <summary>Live PostgreSQL gate for durable Ellai topic history. Same
/// approved disposable loopback target as the other Ellai fixtures. Cleans
/// up its own rows; never touches other data.</summary>
public sealed class EllaiPostgresTopicTests : IAsyncLifetime
{
    private readonly List<string> _sessionIds = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled()) return;
        await using var db = CreateDbContext();
        foreach (var id in _sessionIds)
        {
            await db.Set<EllaiTopicEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
        }
    }

    [EllaiPostgresFact]
    public async Task RetrackConvergesOnOneStableRow()
    {
        const string sessionId = "pg-topic-1";
        _sessionIds.Add(sessionId);
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        var store = new PostgresEllaiTopicStore(db);
        var first = await store.RecordAsync(sessionId, "Queue review", new[] { "queue" },
            null, "v1", null, now, CancellationToken.None);
        var second = await store.RecordAsync(sessionId, "Queue review", new[] { "queue" },
            null, "v2", null, now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(first.TopicId, second.TopicId);
        Assert.Equal("v2", second.EntityVersion);
        Assert.Equal(1, await db.Set<EllaiTopicEntity>().CountAsync(e => e.SessionId == sessionId));
    }

    [EllaiPostgresFact]
    public async Task HistorySurvivesRestartAndStaysSessionScoped()
    {
        const string sessionId = "pg-topic-2";
        _sessionIds.Add(sessionId);
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        await new PostgresEllaiTopicStore(db).RecordAsync(sessionId, "Queue review", new[] { "queue" },
            "case-1", "v3", "chk-1", now, CancellationToken.None);

        // Fresh context (simulated restart) sees the same durable history.
        await using var db2 = CreateDbContext();
        var listed = await new PostgresEllaiTopicStore(db2).ListAsync(sessionId, CancellationToken.None);
        var entry = Assert.Single(listed);
        Assert.Equal("case-1", entry.EntityRef);
        Assert.Equal("chk-1", entry.CheckpointId);

        var other = await new PostgresEllaiTopicStore(db2).ListAsync("pg-topic-other", CancellationToken.None);
        Assert.Empty(other);
    }

    [EllaiPostgresFact]
    public async Task DelayedInsertCannotOverwriteCompetingSnapshot()
    {
        var sessionId = "pg-topic-race-" + Guid.NewGuid().ToString("N");
        _sessionIds.Add(sessionId);
        var gate = new HoldFirstSave();
        await using var delayedDb = CreateDbContext(gate);
        await using var otherDb = CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var delayed = new PostgresEllaiTopicStore(delayedDb).RecordAsync(
            sessionId, "Queue review", ["delayed"], "caller-snapshot", "caller-version",
            null, now, CancellationToken.None);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await new PostgresEllaiTopicStore(otherDb).RecordAsync(
                sessionId, "Queue review", ["other"], "competing-snapshot", "competing-version",
                "preserved-checkpoint", now, CancellationToken.None);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => delayed.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var checkDb = CreateDbContext();
        var stored = Assert.Single(await new PostgresEllaiTopicStore(checkDb).ListAsync(sessionId, CancellationToken.None));
        Assert.Equal("competing-snapshot", stored.EntityRef);
        Assert.Equal("competing-version", stored.EntityVersion);
        Assert.Equal(["other"], stored.Aliases);
        Assert.Equal("preserved-checkpoint", stored.CheckpointId);
    }

    [EllaiPostgresFact]
    public async Task DelayedUpdateCannotOverwriteNewerSnapshot()
    {
        var sessionId = "pg-topic-update-race-" + Guid.NewGuid().ToString("N");
        _sessionIds.Add(sessionId);
        var now = DateTimeOffset.UtcNow;
        await using (var seed = CreateDbContext())
            await new PostgresEllaiTopicStore(seed).RecordAsync(
                sessionId, "Queue review", [], "original", "v0", null, now, CancellationToken.None);

        var gate = new HoldFirstSave();
        await using var delayedDb = CreateDbContext(gate);
        await using var newerDb = CreateDbContext();
        var delayed = new PostgresEllaiTopicStore(delayedDb).RecordAsync(
            sessionId, "Queue review", [], "older", "v1", null, now.AddSeconds(1), CancellationToken.None);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await new PostgresEllaiTopicStore(newerDb).RecordAsync(
                sessionId, "Queue review", [], "newer", "v2", null, now.AddSeconds(2), CancellationToken.None);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => delayed.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var checkDb = CreateDbContext();
        var stored = Assert.Single(await new PostgresEllaiTopicStore(checkDb).ListAsync(sessionId, CancellationToken.None));
        Assert.Equal("newer", stored.EntityRef);
        Assert.Equal("v2", stored.EntityVersion);
    }

    [EllaiPostgresFact]
    public async Task StateToolReportsSnapshotConflictWithoutClaimingRetention()
    {
        var sessionId = "pg-topic-conflict-" + Guid.NewGuid().ToString("N");
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "topic-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var failure = new DbUpdateConcurrencyException("Synthetic conflict");
        await using var db = CreateDbContext(new RejectSave(failure));
        var consent = new EllaiConsentService();
        consent.Grant(sessionId, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: new EmptyQueue(),
            topics: new EllaiTopicService(new PostgresEllaiTopicStore(db)));
        var result = await broker.InvokeAsync(
            new EllaiToolContext(sessionId, "scope", "cases", null, 4, true,
                DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: actor),
            new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string> { ["projection"] = "queue" }),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Contains("could not be retained", result.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("references retained", result.ResultSummary, StringComparison.Ordinal);
    }

    private sealed class EmptyQueue : ICaseQueueQuery
    {
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) =>
            Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));

        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct) =>
            GetMineAsync(actor, limit, ct);
    }

    private sealed class HoldFirstSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _held, 1) == 0)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [EllaiPostgresFact]
    public async Task NonUniqueWriteFailureIsNotTreatedAsConcurrentInsert()
    {
        var failure = new DbUpdateException("Synthetic check failure",
            new PostgresException("Synthetic check failure", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation));
        await using var db = CreateDbContext(new RejectSave(failure));
        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new PostgresEllaiTopicStore(db).RecordAsync(
                "pg-topic-failure-" + Guid.NewGuid().ToString("N"), "Queue review", [],
                null, null, null, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Same(failure, thrown);
    }

    [EllaiPostgresFact]
    public async Task AnotherUniqueConstraintIsNotTreatedAsTopicInsertRace()
    {
        var failure = new DbUpdateException("Synthetic unique failure",
            new PostgresException("Synthetic unique failure", "ERROR", "ERROR",
                PostgresErrorCodes.UniqueViolation, constraintName: "different_constraint"));
        await using var db = CreateDbContext(new RejectSave(failure));
        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new PostgresEllaiTopicStore(db).RecordAsync(
                "pg-topic-failure-" + Guid.NewGuid().ToString("N"), "Queue review", [],
                null, null, null, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Same(failure, thrown);
    }

    [EllaiPostgresFact]
    public async Task ConflictingTopicIdentifierCannotUpdateAnotherSession()
    {
        var sessionId = "pg-topic-collision-" + Guid.NewGuid().ToString("N");
        var foreignSessionId = sessionId + "-foreign";
        _sessionIds.Add(foreignSessionId);
        const string title = "Queue review";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{sessionId}|{title}"));
        var id = "topic-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDbContext();
        db.Set<EllaiTopicEntity>().Add(new EllaiTopicEntity
        {
            TopicId = id,
            SessionId = foreignSessionId,
            Title = title,
            AliasesJson = "[]",
            EntityRef = "foreign-snapshot",
            CoverageState = "Partial",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresEllaiTopicStore(db).RecordAsync(
                sessionId, title, [], "caller-snapshot", null, null, now, CancellationToken.None));
        db.ChangeTracker.Clear();
        var preserved = await db.Set<EllaiTopicEntity>().AsNoTracking().SingleAsync(row => row.TopicId == id);
        Assert.Equal(foreignSessionId, preserved.SessionId);
        Assert.Equal("foreign-snapshot", preserved.EntityRef);
    }

    private sealed class RejectSave(DbUpdateException failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(failure);
    }

    private static bool Enabled() => string.Equals(
        Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES"), "1", StringComparison.Ordinal);

    private static SwiftAidDbContext CreateDbContext(IInterceptor? interceptor = null)
    {
        var value = Environment.GetEnvironmentVariable("SWIFTAID_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The disposable PostgreSQL test connection is required.");
        }

        var builder = new NpgsqlConnectionStringBuilder(value);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            builder.Port != 32703 ||
            !string.Equals(builder.Database, "swiftaid_p21a", StringComparison.Ordinal) ||
            !string.Equals(builder.Username, "p21_migration_owner", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(builder.Password))
        {
            throw new InvalidOperationException("The PostgreSQL fixture target is not the approved disposable loopback database.");
        }

        var options = new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(builder.ConnectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new SwiftAidDbContext(options.Options);
    }
}
