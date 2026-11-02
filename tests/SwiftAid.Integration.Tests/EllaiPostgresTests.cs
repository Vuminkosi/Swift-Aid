using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
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
public sealed class EllaiPostgresTests : IAsyncLifetime
{
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "ellai-pg-test-actor"),
            new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
    private static ActorContext Actor => ActorContext.FromValidatedPrincipal(Principal);
    private readonly List<string> _sessionIds = new();

    public Task InitializeAsync() => Task.CompletedTask;

    [EllaiPostgresFact]
    public async Task LatestGuidanceLookupSpansHistoryWindowAndEnforcesOwnerAndOutcome()
    {
        var id = $"guidance-history-{Guid.NewGuid():N}";
        _sessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        var proposal = EllaiUiProposalPayload.Serialize([new(UiIntentKind.StartGuidance, "ellai.guidance", null, null,
            new Dictionary<string, string> { ["feature"] = "report", ["objective"] = "Show report form" })]);
        await using (var db = CreateDbContext())
        {
            await new PostgresEllaiSessionStore(db).CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
                Actor.ActorId, "scope-test", null, null, now), default);
            for (var index = 0; index < 108; index++)
                db.Set<EllaiUtteranceEntity>().Add(new()
                {
                    SessionId = id, ClientUtteranceId = $"turn-{index}", Sequence = index + 1,
                    AcceptedAtUtc = now, CompletedAtUtc = now, AssistantText = "Synthetic response",
                    Outcome = index == 107 ? "TerminalFailure" : "Completed",
                    UiProposalsJson = index is 0 or 1 or 107 ? proposal : null,
                });
            await db.SaveChangesAsync();
        }
        await using var restoredDb = CreateDbContext();
        var restored = new PostgresEllaiSessionStore(restoredDb);
        Assert.Equal(100, (await restored.ReadConversationAsync(id, Actor.ActorId, default)).Count);
        var latest = await restored.ReadLatestGuidanceTurnAsync(id, Actor.ActorId, default);
        Assert.Equal("turn-1", latest!.ClientUtteranceId);
        Assert.Null(await restored.ReadLatestGuidanceTurnAsync(id, "foreign-owner", default));
        Assert.Null(await restored.ReadLatestGuidanceTurnAsync("missing-session", Actor.ActorId, default));
    }

    [EllaiPostgresFact]
    public async Task GuidanceCheckpointSurvivesFreshContextAndRejectsStaleConcurrentUpdate()
    {
        var id = $"guidance-{Guid.NewGuid():N}";
        _sessionIds.Add(id);
        var checkpoint = new EllaiGuidanceCheckpoint("proposal", "context", "scope", 1, 1,
            "generation", "Generating", DateTimeOffset.UtcNow.AddMinutes(2), null, null, [], [], "report");
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
            "guidance-owner", "scope", null, null, DateTimeOffset.UtcNow)
        {
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint),
        };
        await using (var createDb = CreateDbContext())
            await new PostgresEllaiSessionStore(createDb).CreateAsync(session, CancellationToken.None);

        await using var firstDb = CreateDbContext();
        await using var staleDb = CreateDbContext();
        var first = new PostgresEllaiSessionStore(firstDb);
        var stale = new PostgresEllaiSessionStore(staleDb);
        var loaded = (await first.GetAsync(id, CancellationToken.None))!;
        Assert.Equal("Generating", EllaiGuidanceCheckpoint.Deserialize(loaded.GuidanceJson)!.Status);
        var staleRecord = (await stale.GetAsync(id, CancellationToken.None))!;
        Assert.NotNull(await first.TryUpdateAsync(loaded with
        {
            SessionVersion = 2,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint with { Version = 2, Status = "Cancelled" }),
        }, 1, CancellationToken.None));
        Assert.Null(await stale.TryUpdateAsync(staleRecord with { SessionVersion = 2 }, 1, CancellationToken.None));

        await using var recoveredDb = CreateDbContext();
        var recovered = new PostgresEllaiSessionStore(recoveredDb);
        var ownerRows = await recovered.ListOwnedAsync("guidance-owner", 0, 20, CancellationToken.None);
        var restored = EllaiGuidanceCheckpoint.Deserialize(Assert.Single(ownerRows).GuidanceJson)!;
        Assert.Equal("Cancelled", restored.Status);
        Assert.Equal(2, restored.Version);
        Assert.Empty(await recovered.ListOwnedAsync("foreign-owner", 0, 20, CancellationToken.None));
    }

    [EllaiPostgresFact]
    public async Task SessionHistorySurvivesNewContextAndPagesOnlyOwnedRows()
    {
        var owner = $"history-owner-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDbContext())
        {
            var store = new PostgresEllaiSessionStore(db);
            for (var i = 0; i < 4; i++)
            {
                var id = $"history-{Guid.NewGuid():N}";
                _sessionIds.Add(id);
                await store.CreateAsync(new(id, EllaiMode.Ask, "Cancelled", 1, 1, 1, 1, 0, 12,
                    i == 3 ? "another-history-owner" : owner, "scope-test", null, null, now.AddMinutes(i)), CancellationToken.None);
            }
        }
        await using var restarted = CreateDbContext();
        var restored = new PostgresEllaiSessionStore(restarted);
        var first = await restored.ListOwnedAsync(owner, 0, 2, CancellationToken.None);
        var second = await restored.ListOwnedAsync(owner, 2, 2, CancellationToken.None);
        Assert.Equal(new[] { _sessionIds[2], _sessionIds[1] }, first.Select(item => item.SessionId));
        Assert.Equal(_sessionIds[0], Assert.Single(second).SessionId);
        Assert.Empty(await restored.ListOwnedAsync("unknown-history-owner", 0, 20, CancellationToken.None));
    }

    [EllaiPostgresFact]
    public async Task DurableConversationSurvivesRestartAndRejectsStaleAcknowledgement()
    {
        var id = $"pg-conversation-{Guid.NewGuid():N}";
        _sessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using var firstDb = CreateDbContext();
        var first = new PostgresEllaiSessionStore(firstDb);
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, Actor.ActorId, "scope-test", null, null, now);
        await first.CreateAsync(session, CancellationToken.None);
        var next = session with { SessionVersion = 2, CyclesUsed = 1 };
        const string payload = "{\"utterance\":\"Synthetic question\"}";
        Assert.NotNull(await first.TryAcceptUtteranceAsync(next, 1, "u-1", CancellationToken.None, payload));
        var claim = (await first.ClaimNextAsync(now, now.AddSeconds(1), CancellationToken.None))!;

        await using var secondDb = CreateDbContext();
        var second = new PostgresEllaiSessionStore(secondDb);
        var retry = (await second.ClaimNextAsync(now.AddSeconds(2), now.AddMinutes(1), CancellationToken.None))!;
        Assert.Equal(2, retry.AttemptCount);
        Assert.Null(await first.CompleteAsync(id, "u-1", claim.AttemptCount, now, CancellationToken.None,
            EllaiUtteranceResolution.Completed("Stale response", 1)));
        Assert.NotNull(await second.CompleteAsync(id, "u-1", retry.AttemptCount, now, CancellationToken.None,
            EllaiUtteranceResolution.Completed("Validated synthetic response", 1) with
            {
                UiProposalsJson = EllaiUiProposalPayload.Serialize([new(UiIntentKind.StartGuidance, "report", null, null,
                    new Dictionary<string, string> { ["objective"] = "Create a report" })])
            }));

        await using var restartedDb = CreateDbContext();
        var restarted = new PostgresEllaiSessionStore(restartedDb);
        var turn = Assert.Single(await restarted.ReadConversationAsync(id, Actor.ActorId, CancellationToken.None));
        Assert.Equal("Completed", turn.Outcome);
        Assert.Equal("Synthetic question", turn.UserText);
        Assert.Equal("Validated synthetic response", turn.AssistantText);
        Assert.Equal(UiIntentKind.StartGuidance, Assert.Single(EllaiUiProposalPayload.Deserialize(turn.UiProposalsJson!)).Kind);
        Assert.Empty(await restarted.ReadConversationAsync(id, "wrong-owner", CancellationToken.None));
        var row = await restartedDb.Set<EllaiUtteranceEntity>().SingleAsync(turn => turn.SessionId == id);
        Assert.Null(row.WorkPayloadJson);
        Assert.NotNull(row.CompletedAtUtc);
        Assert.Null(await restarted.ClaimNextAsync(now.AddMinutes(2), now.AddMinutes(3), CancellationToken.None));
        Assert.True((await restarted.TryAcceptUtteranceAsync(next, 1, "u-1", CancellationToken.None, payload))!.Replay);
    }

    [EllaiPostgresFact]
    public async Task CancellationFencesCompletedAnswerAndRetryFailureIsObservable()
    {
        var id = $"pg-outcome-{Guid.NewGuid():N}";
        _sessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDbContext();
        var store = new PostgresEllaiSessionStore(db);
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, Actor.ActorId, "scope-test", null, null, now);
        await store.CreateAsync(session, CancellationToken.None);
        session = session with { SessionVersion = 2, CyclesUsed = 1 };
        await store.TryAcceptUtteranceAsync(session, 1, "u-1", CancellationToken.None, "{\"utterance\":\"Question\"}");
        var claim = (await store.ClaimNextAsync(now, now.AddSeconds(1), CancellationToken.None))!;
        await store.FailAsync(id, "u-1", claim.AttemptCount, now, false, CancellationToken.None);
        Assert.Equal("RetryableFailure", (await store.ReadConversationAsync(id, Actor.ActorId, CancellationToken.None))[0].Outcome);
        await store.TryUpdateAsync(session with { Status = "Cancelled", SessionVersion = 3 }, 2, CancellationToken.None);
        var result = await store.CompleteAsync(id, "u-1", claim.AttemptCount, now, CancellationToken.None,
            EllaiUtteranceResolution.Completed("Late answer", 1));
        Assert.Equal("Cancelled", result!.Outcome);
        Assert.Null((await store.ReadConversationAsync(id, Actor.ActorId, CancellationToken.None))[0].AssistantText);
    }

    [EllaiPostgresFact]
    public async Task SupersededFailureIsCommittedAsCancellation()
    {
        var id = $"pg-superseded-{Guid.NewGuid():N}";
        _sessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDbContext();
        var store = new PostgresEllaiSessionStore(db);
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, Actor.ActorId, "scope-test", null, null, now);
        await store.CreateAsync(session, CancellationToken.None);
        Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, "u-1",
            CancellationToken.None, "{\"utterance\":\"Synthetic question\"}"));
        var claim = (await store.ClaimNextAsync(now, now.AddMinutes(1), CancellationToken.None))!;
        Assert.NotNull(await store.TryUpdateAsync(session with { SessionVersion = 3, InteractionEpoch = 2, CyclesUsed = 1 },
            2, CancellationToken.None));
        var result = await store.CompleteAsync(id, "u-1", claim.AttemptCount, now, CancellationToken.None,
            EllaiUtteranceResolution.Failed with { ExpectedInteractionEpoch = 1 });
        Assert.Equal("Cancelled", result!.Outcome);
        await using var reconnectedDb = CreateDbContext();
        var restored = Assert.Single(await new PostgresEllaiSessionStore(reconnectedDb)
            .ReadConversationAsync(id, Actor.ActorId, CancellationToken.None));
        Assert.Equal("Cancelled", restored.Outcome);
        Assert.Null(restored.AssistantText);
    }

    public async Task DisposeAsync()
    {
        if (!Enabled()) return;
        await using var db = CreateDbContext();
        foreach (var id in _sessionIds)
        {
            await db.Set<EllaiUsageSampleEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
            await db.Set<EllaiConsentGrantEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
            await db.Set<EllaiUtteranceEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
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
            Principal, CancellationToken.None);
        _sessionIds.Add(view.SessionId);

        var accepted = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            Actor, CancellationToken.None);
        Assert.NotNull(accepted);

        var checkpoint = await service.SaveCheckpointAsync(
            view.SessionId, "turn-1", new[] { "topic-1" }, Array.Empty<string>(),
            CancellationToken.None);
        Assert.NotNull(checkpoint);

        // Replay of the accepted utterance cannot duplicate state.
        var replay = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            Actor, CancellationToken.None);
        Assert.NotNull(replay);
        Assert.True(replay.Replay);

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
            Principal, CancellationToken.None);
        _sessionIds.Add(view.SessionId);

        await using var dbA = CreateDbContext();
        await using var dbB = CreateDbContext();
        var serviceA = new EllaiSessionService(new PostgresEllaiSessionStore(dbA));
        var serviceB = new EllaiSessionService(new PostgresEllaiSessionStore(dbB));

        var first = await serviceA.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-a", view.SessionId, view.SessionVersion,
                "first", null, "typed", 1),
            Actor, CancellationToken.None);
        Assert.NotNull(first);

        var stale = await serviceB.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-b", view.SessionId, view.SessionVersion,
                "stale", null, "typed", 1),
            Actor, CancellationToken.None);
        Assert.Null(stale);
    }

    [EllaiPostgresFact]
    public async Task ReplayedUtteranceIdentitySurvivesRestartWithoutNewCycle()
    {
        await using var db = CreateDbContext();
        var service = new EllaiSessionService(new PostgresEllaiSessionStore(db));
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "pg-4", "cases", null, "text", "text"),
            Principal, CancellationToken.None);
        _sessionIds.Add(view.SessionId);

        var first = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            Actor, CancellationToken.None);
        Assert.NotNull(first);
        Assert.False(first.Replay);

        // Fresh context (simulated restart) replays the identity: same view,
        // no new cycle.
        await using var db2 = CreateDbContext();
        var replay = await new EllaiSessionService(new PostgresEllaiSessionStore(db2)).SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            Actor, CancellationToken.None);
        Assert.NotNull(replay);
        Assert.True(replay.Replay);
        Assert.Equal(1, replay.View.CyclesUsed);

        var claims = await db2.Set<EllaiUtteranceEntity>()
            .CountAsync(e => e.SessionId == view.SessionId);
        Assert.Equal(1, claims);
    }

    [EllaiPostgresFact]
    public async Task ConsentGrantsSurviveRestartAndRevokeDurably()
    {
        const string sessionId = "pg-consent-1";
        _sessionIds.Add(sessionId);

        await using var db = CreateDbContext();
        var store = new PostgresEllaiConsentStore(db);
        var grant = new EllaiConsentGrant(
            "grant-pg-1", sessionId, EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", 7,
            DateTimeOffset.UtcNow.AddHours(1), false);
        await store.RecordAsync(grant, CancellationToken.None);
        await store.RecordAsync(grant, CancellationToken.None);

        // Fresh context (simulated restart) hydrates the same grant.
        await using var db2 = CreateDbContext();
        var fresh = new EllaiConsentService();
        await fresh.HydrateAsync(sessionId, new PostgresEllaiConsentStore(db2), CancellationToken.None);
        Assert.True(fresh.IsGranted(
            sessionId, EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", DateTimeOffset.UtcNow));

        Assert.True(await store.SetRevokedAsync(sessionId, "grant-pg-1", CancellationToken.None));
        Assert.False(await store.SetRevokedAsync(sessionId, "grant-missing", CancellationToken.None));

        await using var db3 = CreateDbContext();
        var revoked = new EllaiConsentService();
        await revoked.HydrateAsync(sessionId, new PostgresEllaiConsentStore(db3), CancellationToken.None);
        Assert.False(revoked.IsGranted(
            sessionId, EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", DateTimeOffset.UtcNow));
    }

    [EllaiPostgresFact]
    public async Task UsageSamplesSurviveRestartWithSpend()
    {
        const string sessionId = "pg-usage-1";
        _sessionIds.Add(sessionId);

        await using var db = CreateDbContext();
        var store = new PostgresEllaiUsageStore(db);
        await store.RecordAsync(
            new StoredUsageSample(sessionId, 1000, 500, 0.02m, DateTimeOffset.UtcNow),
            CancellationToken.None);

        // Fresh context (simulated restart) derives the same totals.
        await using var db2 = CreateDbContext();
        var fresh = new PostgresEllaiUsageStore(db2);
        var totals = await fresh.TotalsAsync(sessionId, CancellationToken.None);
        Assert.Equal(1500, totals.TotalTokens);
        Assert.Equal(1, totals.Calls);
        Assert.Equal(0.02m, await fresh.SpendForAsync(sessionId, CancellationToken.None));
        Assert.Equal(0.02m, await fresh.TotalSpendAsync(CancellationToken.None));

        var missing = await fresh.TotalsAsync("pg-usage-absent", CancellationToken.None);
        Assert.Equal(0, missing.Calls);
    }

    [EllaiPostgresFact]
    public async Task CancelledSessionStaysCancelledAcrossContexts()
    {
        await using var db = CreateDbContext();
        var service = new EllaiSessionService(new PostgresEllaiSessionStore(db));
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "pg-3", "cases", null, "text", "text"),
            Principal, CancellationToken.None);
        _sessionIds.Add(view.SessionId);

        Assert.True(await service.CancelAsync(view.SessionId, view.SessionVersion, Principal, CancellationToken.None));

        await using var db2 = CreateDbContext();
        var service2 = new EllaiSessionService(new PostgresEllaiSessionStore(db2));
        var after = await service2.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion + 1,
                "hello?", null, "typed", 2),
            Actor, CancellationToken.None);
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
