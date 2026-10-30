using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

/// <summary>Live PostgreSQL gate for durable Ellai topic history. Same
/// approved disposable loopback target as the other Ellai fixtures. Cleans
/// up its own rows; never touches other data.</summary>
public sealed class EllaiPostgresTopicTests : IAsyncDisposable
{
    private readonly List<string> _sessionIds = new();

    public async ValueTask DisposeAsync()
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

    private static bool Enabled() => string.Equals(
        Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES"), "1", StringComparison.Ordinal);

    private static SwiftAidDbContext CreateDbContext()
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

        return new SwiftAidDbContext(
            new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(builder.ConnectionString).Options);
    }
}
