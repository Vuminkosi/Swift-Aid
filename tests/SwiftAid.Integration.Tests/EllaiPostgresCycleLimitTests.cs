using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

/// <summary>PostgreSQL regression for the authoritative turn-cycle boundary.
/// It uses only the repository's opt-in disposable loopback fixture.</summary>
public sealed class EllaiPostgresCycleLimitTests
{
    [EllaiPostgresFact]
    public async Task ExhaustedSessionDoesNotAdvanceOrClaimUtterance()
    {
        var sessionId = $"pg-cycle-limit-{Guid.NewGuid():N}";
        var ownerActorId = $"actor-{Guid.NewGuid():N}";
        const long expectedVersion = 4;
        const int cycleLimit = 2;

        try
        {
            await using (var db = CreateDbContext())
            {
                var store = new PostgresEllaiSessionStore(db);
                await store.CreateAsync(
                    new EllaiSessionRecord(
                        sessionId,
                        EllaiMode.Ask,
                        "Active",
                        expectedVersion,
                        ControlFence: 1,
                        InteractionEpoch: 1,
                        SpeechEpoch: 1,
                        CyclesUsed: cycleLimit,
                        CyclesAllowed: cycleLimit,
                        OwnerActorId: ownerActorId,
                        ScopeEpoch: "scope-test",
                        ActiveInvestigationId: null,
                        PendingCheckpointId: null,
                        UpdatedAtUtc: DateTimeOffset.UtcNow),
                    CancellationToken.None);

                var rejected = await store.TryAcceptUtteranceAsync(
                    new EllaiSessionRecord(
                        sessionId,
                        EllaiMode.Ask,
                        "Active",
                        expectedVersion + 1,
                        ControlFence: 1,
                        InteractionEpoch: 2,
                        SpeechEpoch: 1,
                        CyclesUsed: cycleLimit + 1,
                        CyclesAllowed: cycleLimit,
                        OwnerActorId: ownerActorId,
                        ScopeEpoch: "scope-test",
                        ActiveInvestigationId: null,
                        PendingCheckpointId: null,
                        UpdatedAtUtc: DateTimeOffset.UtcNow),
                    expectedVersion,
                    "exhausted-cycle-utterance",
                    CancellationToken.None);

                Assert.Null(rejected);
            }

            await using var verifyDb = CreateDbContext();
            var verificationStore = new PostgresEllaiSessionStore(verifyDb);
            var session = await verificationStore.GetAsync(sessionId, CancellationToken.None);
            Assert.NotNull(session);
            Assert.Equal(expectedVersion, session.SessionVersion);
            Assert.Equal(cycleLimit, session.CyclesUsed);
            Assert.Equal(cycleLimit, session.CyclesAllowed);
            Assert.False(await verifyDb.Set<EllaiUtteranceEntity>()
                .AnyAsync(entity => entity.SessionId == sessionId));
        }
        finally
        {
            await using var cleanupDb = CreateDbContext();
            await cleanupDb.Set<EllaiUtteranceEntity>()
                .Where(entity => entity.SessionId == sessionId)
                .ExecuteDeleteAsync();
            await cleanupDb.Set<EllaiCheckpointEntity>()
                .Where(entity => entity.SessionId == sessionId)
                .ExecuteDeleteAsync();
            await cleanupDb.Set<EllaiSessionEntity>()
                .Where(entity => entity.SessionId == sessionId)
                .ExecuteDeleteAsync();
        }
    }

    private static SwiftAidDbContext CreateDbContext()
    {
        var value = Environment.GetEnvironmentVariable("SWIFTAID_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The disposable PostgreSQL test connection is required.");
        }

        var builder = new NpgsqlConnectionStringBuilder(value);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1")
            || builder.Port != 32703
            || !string.Equals(builder.Database, "swiftaid_p21a", StringComparison.Ordinal)
            || !string.Equals(builder.Username, "p21_migration_owner", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(builder.Password))
        {
            throw new InvalidOperationException(
                "The PostgreSQL fixture target is not the approved disposable loopback database.");
        }

        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;
        return new SwiftAidDbContext(options);
    }
}
