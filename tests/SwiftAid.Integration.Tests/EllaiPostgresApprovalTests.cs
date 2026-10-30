using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Application.Ellai;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

/// <summary>Live PostgreSQL gate for the durable Ellai approval/receipt
/// authority. Same approved disposable loopback target as the session
/// fixtures. Cleans up its own rows; never touches other data.</summary>
public sealed class EllaiPostgresApprovalTests : IAsyncLifetime
{
    private readonly List<string> _sessionIds = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled()) return;
        await using var db = CreateDbContext();
        foreach (var id in _sessionIds)
        {
            await db.Set<EllaiPlanApprovalEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
        }
    }

    [EllaiPostgresFact]
    public async Task RecordIsIdempotentByKeyAcrossContexts()
    {
        const string sessionId = "pg-approval-1";
        _sessionIds.Add(sessionId);
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        var store = new PostgresEllaiApprovalStore(db);
        var first = await store.RecordAsync(sessionId, "action-1", "key-1", "hash-1", 1,
            "officer-1", null, now, TimeSpan.FromMinutes(15), "policy-hash", CancellationToken.None);

        await using var db2 = CreateDbContext();
        var retry = await new PostgresEllaiApprovalStore(db2).RecordAsync(sessionId, "action-1", "key-1",
            "hash-1", 1, "officer-1", null, now.AddMinutes(1), TimeSpan.FromMinutes(15),
            "policy-hash", CancellationToken.None);

        Assert.Equal(first.ReceiptId, retry.ReceiptId);
        Assert.Equal("officer-1", retry.ApprovedRecipient);
        Assert.Equal(1, await db2.Set<EllaiPlanApprovalEntity>().CountAsync(e => e.SessionId == sessionId));
    }

    [EllaiPostgresFact]
    public async Task ConflictingPlanActionBindingFails()
    {
        const string sessionId = "pg-approval-2";
        _sessionIds.Add(sessionId);
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        var store = new PostgresEllaiApprovalStore(db);
        await store.RecordAsync(sessionId, "action-1", "key-1", "hash-1", 1,
            null, null, now, TimeSpan.FromMinutes(15), string.Empty, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordAsync(sessionId, "action-1", "key-2", "hash-1", 1,
                null, null, now, TimeSpan.FromMinutes(15), string.Empty, CancellationToken.None));
    }

    [EllaiPostgresFact]
    public async Task ReceiptSurvivesRestartAndHonoursExpiry()
    {
        const string sessionId = "pg-approval-3";
        _sessionIds.Add(sessionId);
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        var recorded = await new PostgresEllaiApprovalStore(db).RecordAsync(
            sessionId, "action-1", "key-1", "hash-1", 1,
            null, null, now, TimeSpan.FromMinutes(15), string.Empty, CancellationToken.None);

        // Fresh context (simulated restart) sees the same durable receipt.
        await using var db2 = CreateDbContext();
        var store2 = new PostgresEllaiApprovalStore(db2);
        var reloaded = await store2.GetAsync(recorded.ReceiptId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(EllaiReceiptStatus.Accepted, reloaded.Status);

        var applied = await store2.MarkStatusAsync(
            recorded.ReceiptId, EllaiReceiptStatus.Applied, now.AddMinutes(5), CancellationToken.None);
        Assert.Equal(EllaiReceiptStatus.Applied, applied?.Status);

        var late = await store2.MarkStatusAsync(
            recorded.ReceiptId, EllaiReceiptStatus.Applied, now.AddMinutes(16), CancellationToken.None);
        Assert.Equal(EllaiReceiptStatus.UnknownOutcome, late?.Status);

        var listed = await store2.ListBySessionAsync(sessionId, CancellationToken.None);
        Assert.Single(listed);
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
