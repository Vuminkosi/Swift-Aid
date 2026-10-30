using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class EllaiPostgresFactAttribute : FactAttribute
{
    public EllaiPostgresFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES"), "1", StringComparison.Ordinal))
        {
            Skip = "Set SWIFTAID_RUN_SQLAUTH_FIXTURES=1 to run the disposable PostgreSQL Ellai fixtures.";
        }
    }
}

/// <summary>Live PostgreSQL gate for the Ellai session store (ELL-T04).
/// Same approved disposable loopback target as the authority fixtures.
/// Cleans up its own rows; never touches other data.</summary>
public sealed class EllaiPostgresTests : IAsyncDisposable
{
    private readonly List<string> _sessionIds = new();

    public async ValueTask DisposeAsync()
    {
        if (!Enabled()) return;
        await using var db = CreateDbContext();
        foreach (var id in _sessionIds)
        {
            await db.Set<EllaiCheckpointEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
            await db.Set<EllaiSessionEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
        }
    }

    [EllaiPostgresFact]
    public async Task CrashAfterAcceptedTurnRestoresExactlyOneCheckpoint()
    {
        await using var db = CreateDbContext();
        var service = new EllaiSessionService(new PostgresEllaiSessionStore(db));
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "pg-1", "cases", null, "text", "text"),
            CancellationToken.None);
        _sessionIds.Add(view.SessionId);

        var accepted = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            CancellationToken.None);
        Assert.NotNull(accepted);

        var checkpoint = await service.SaveCheckpointAsync(
            view.SessionId, "turn-1", new[] { "topic-1" }, Array.Empty<string>(),
            CancellationToken.None);
        Assert.NotNull(checkpoint);

        // Replay of the accepted utterance cannot duplicate state.
        var replay = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            CancellationToken.None);
        Assert.Null(replay);

        // Fresh context (simulated restart) sees exactly one checkpoint.
        await using var db2 = CreateDbContext();
        var count = await db2.Set<EllaiCheckpointEntity>()
            .CountAsync(e => e.SessionId == view.SessionId);
        Assert.Equal(1, count);
        var reloaded = await new PostgresEllaiSessionStore(db2).GetAsync(view.SessionId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(checkpoint.CheckpointId, reloaded.PendingCheckpointId);
    }

    [EllaiPostgresFact]
    public async Task ConcurrentStaleFenceIsRejected()
    {
        await using var db = CreateDbContext();
        var service = new EllaiSessionService(new PostgresEllaiSessionStore(db));
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "pg-2", "cases", null, "text", "text"),
            CancellationToken.None);
        _sessionIds.Add(view.SessionId);

        await using var dbA = CreateDbContext();
        await using var dbB = CreateDbContext();
        var serviceA = new EllaiSessionService(new PostgresEllaiSessionStore(dbA));
        var serviceB = new EllaiSessionService(new PostgresEllaiSessionStore(dbB));

        var first = await serviceA.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-a", view.SessionId, view.SessionVersion,
                "first", null, "typed", 1),
            CancellationToken.None);
        Assert.NotNull(first);

        var stale = await serviceB.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-b", view.SessionId, view.SessionVersion,
                "stale", null, "typed", 1),
            CancellationToken.None);
        Assert.Null(stale);
    }

    [EllaiPostgresFact]
    public async Task CancelledSessionStaysCancelledAcrossContexts()
    {
        await using var db = CreateDbContext();
        var service = new EllaiSessionService(new PostgresEllaiSessionStore(db));
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "pg-3", "cases", null, "text", "text"),
            CancellationToken.None);
        _sessionIds.Add(view.SessionId);

        Assert.True(await service.CancelAsync(view.SessionId, view.SessionVersion, CancellationToken.None));

        await using var db2 = CreateDbContext();
        var service2 = new EllaiSessionService(new PostgresEllaiSessionStore(db2));
        var after = await service2.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion + 1,
                "hello?", null, "typed", 2),
            CancellationToken.None);
        Assert.Null(after);
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
