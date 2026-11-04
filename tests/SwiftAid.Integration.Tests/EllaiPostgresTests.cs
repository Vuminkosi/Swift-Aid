using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Intelligence.Meta;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Caching;
using SwiftAid.Infrastructure.Configuration;
using StackExchange.Redis;
using System.Net;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;

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

public sealed class FrameworkPostgresFactAttribute : FactAttribute
{
    public FrameworkPostgresFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES"), "1", StringComparison.Ordinal))
            Skip = "Set SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES=1 for the dedicated disposable framework database.";
    }
}

public sealed class FrameworkPostgresRedisDownFactAttribute : FactAttribute
{
    public FrameworkPostgresRedisDownFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_REDIS_DOWN_CONNECTION")))
            Skip = "Requires the dedicated PostgreSQL fixture and a stopped disposable Redis endpoint.";
    }
}

public sealed class FrameworkPostgresLiveMuseFactAttribute : FactAttribute
{
    public FrameworkPostgresLiveMuseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES") != "1" ||
            Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1")
            Skip = "Requires disposable framework PostgreSQL and explicitly enabled live Muse.";
    }
}

/// <summary>Live PostgreSQL gate for the Ellai session store (ELL-T04).
/// Same approved disposable loopback target as the authority fixtures.
/// Cleans up its own rows; never touches other data.</summary>
[Collection("Ellai framework PostgreSQL worker")]
public sealed class EllaiPostgresTests : IAsyncLifetime
{
    [FrameworkPostgresFact]
    public async Task ConcurrentFrameworkFixtureMigrationsCompleteWithoutRacing()
    {
        await using var first = CreateFrameworkDbContext();
        await using var second = CreateFrameworkDbContext();
        await Task.WhenAll(CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(first),
            CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(second));
        Assert.Empty(await first.Database.GetPendingMigrationsAsync());
    }

    [FrameworkPostgresFact]
    public Task FrameworkHistoryRetainsTwelveShortOwnedTurns() => VerifyLongFrameworkHistoryAsync(false);

    [FrameworkPostgresLiveMuseFact]
    public Task LiveMuseRecallsLongOwnedHistory() => VerifyLongFrameworkHistoryAsync(true);

    private async Task VerifyLongFrameworkHistoryAsync(bool liveModel)
    {
        var id = $"framework-long-history-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        var question = liveModel ? "What code word did I ask you to remember?" : "follow-up";
        var work = new EllaiUtteranceWork(id, "scope-test", question, "public.product-help", null,
            EllaiMode.Ask, 1, "cycle-current", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "current");
        await using var db = CreateFrameworkDbContext();
        await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
        var store = new PostgresEllaiSessionStore(db);
        await store.CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 12, 12,
            Actor.ActorId, "scope-test", null, null, now), default);
        for (var index = 1; index <= 12; index++)
            db.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = $"prior-{index}", Sequence = index,
                UserText = index == 1 && liveModel ? "Remember the code word cobalt." : $"question-{index}",
                AssistantText = $"answer-{index} {(index == 1 && liveModel ? "The code word is cobalt. " : "")}{new string('x', 1000)}",
                Outcome = "Completed", AttemptCount = 1, AcceptedAtUtc = now, CompletedAtUtc = now,
                ToolTraceJson = EllaiToolTrace.SerializeFlattened([], false, [])
            });
        db.Set<EllaiUtteranceEntity>().Add(new()
        {
            SessionId = id, ClientUtteranceId = "current", Sequence = 13,
            UserText = question, Outcome = "Queued", AcceptedAtUtc = now,
            WorkPayloadJson = EllaiUtteranceWorkEnvelope.Serialize(work, "current")
        });
        await db.SaveChangesAsync();

        var rows = await store.ReadConversationForExecutionAsync(id, Actor.ActorId, "current", default);
        var history = await EllaiAuthorizedConversationContext.BuildAsync(rows, "current", Actor, null, default);
        Assert.Equal(12, history.RetainedTurnIds.Count);
        Assert.Contains(history.Messages, message => message.Content.StartsWith("answer-1 ", StringComparison.Ordinal));
        Assert.Contains(history.Messages, message => message.Content.StartsWith("answer-12 ", StringComparison.Ordinal));
        Assert.Empty(await store.ReadConversationForExecutionAsync(id, "foreign-owner", "current", default));

        using var model = new RecoveryClient();
        using var live = liveModel ? EllaiMuseChatClientFactory.Create(EllaiOptions.Bind(Environment.GetEnvironmentVariable)) : null;
        await RunFrameworkWorkerAsync(id, "current", live ?? model, null, liveModel ? TimeSpan.FromMinutes(2) : null);
        var completed = (await store.ReadConversationForExecutionAsync(id, Actor.ActorId, "current", default))
            .Single(turn => turn.ClientUtteranceId == "current");
        Assert.Equal("Completed", completed.Outcome);
        Assert.True(EllaiToolTrace.TryRead(completed.ToolTraceJson!, out _));
        if (liveModel)
            Assert.Contains("cobalt", completed.AssistantText!, StringComparison.OrdinalIgnoreCase);
        else
        {
            Assert.Contains("question-1", model.LastMessages);
            Assert.Contains(model.LastMessages, message => message.StartsWith("answer-1 ", StringComparison.Ordinal));
            Assert.Contains("question-12", model.LastMessages);
            Assert.Contains(model.LastMessages, message => message.StartsWith("answer-12 ", StringComparison.Ordinal));
            Assert.Contains("follow-up", model.LastMessages);
        }
    }

    [FrameworkPostgresFact]
    public async Task ExistingSessionContinuesBeyondCycleThreshold()
    {
        var id = $"framework-cycle-threshold-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        await using var db = CreateFrameworkDbContext();
        await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
        var store = new PostgresEllaiSessionStore(db);
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 12, 12,
            Actor.ActorId, "scope-test", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var accepted = await store.TryAcceptUtteranceAsync(session with
        {
            SessionVersion = 2, InteractionEpoch = 2, CyclesUsed = 13
        }, 1, "threshold-followup", default);
        Assert.NotNull(accepted);
        Assert.Equal(13, (await store.GetAsync(id, default))!.CyclesUsed);
        Assert.Null(await store.TryAcceptUtteranceAsync(session with
        {
            SessionVersion = 2, InteractionEpoch = 2, CyclesUsed = 13
        }, 1, "stale-followup", default));
    }

    [FrameworkPostgresFact]
    public Task ExecutionHistoryKeepsItsAnchorWhenMoreThanOneHundredLaterTurnsAreQueued() =>
        VerifyAnchoredExecutionHistoryAsync(liveModel: false);

    [FrameworkPostgresFact]
    public Task FrameworkWorkerReceivesMoreThanOneHundredCompletedTurns() => VerifyManyTurnsAsync(false);

    [FrameworkPostgresLiveMuseFact]
    public Task LiveMuseRecallsFirstOfMoreThanOneHundredTurns() => VerifyManyTurnsAsync(true);

    private async Task VerifyManyTurnsAsync(bool liveModel)
    {
        var id = $"framework-many-turns-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        var question = liveModel ? "What code word did I ask you to remember?" : "follow-up";
        var work = new EllaiUtteranceWork(id, "scope-test", question, "public.product-help", null,
            EllaiMode.Ask, 1, "cycle-current", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "current");
        await using var db = CreateFrameworkDbContext();
        await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
        var store = new PostgresEllaiSessionStore(db);
        await store.CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 105, 12,
            Actor.ActorId, "scope-test", null, null, now), default);
        for (var index = 1; index <= 105; index++)
            db.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = $"prior-{index}", Sequence = index,
                UserText = index == 1 && liveModel ? "Remember the code word cobalt." : $"question-{index}",
                AssistantText = index == 1 && liveModel ? "I will remember cobalt." : $"answer-{index}", Outcome = "Completed",
                AttemptCount = 1, AcceptedAtUtc = now, CompletedAtUtc = now,
                ToolTraceJson = EllaiToolTrace.SerializeFlattened([], false, [])
            });
        db.Set<EllaiUtteranceEntity>().Add(new()
        {
            SessionId = id, ClientUtteranceId = "current", Sequence = 106, UserText = question,
            Outcome = "Queued", AcceptedAtUtc = now, WorkPayloadJson = EllaiUtteranceWorkEnvelope.Serialize(work, "current")
        });
        await db.SaveChangesAsync();

        var rows = await store.ReadConversationForExecutionAsync(id, Actor.ActorId, "current", default);
        Assert.Equal(106, rows.Count);
        Assert.Empty(await store.ReadConversationForExecutionAsync(id, "foreign-owner", "current", default));
        using var model = new RecoveryClient();
        using var live = liveModel ? EllaiMuseChatClientFactory.Create(EllaiOptions.Bind(Environment.GetEnvironmentVariable)) : null;
        await RunFrameworkWorkerAsync(id, "current", live ?? model, null, liveModel ? TimeSpan.FromMinutes(2) : null);
        var completed = (await store.ReadConversationForExecutionAsync(id, Actor.ActorId, "current", default))
            .Single(turn => turn.ClientUtteranceId == "current");
        Assert.Equal("Completed", completed.Outcome);
        if (liveModel)
            Assert.Contains("cobalt", completed.AssistantText!, StringComparison.OrdinalIgnoreCase);
        else
        {
            Assert.Contains("question-1", model.LastMessages);
            Assert.Contains("answer-1", model.LastMessages);
            Assert.Contains("question-105", model.LastMessages);
            Assert.Contains("answer-105", model.LastMessages);
        }
    }

    [FrameworkPostgresFact]
    public async Task FailedTurnsDoNotEvictCompletedExecutionHistory()
    {
        var id = $"framework-failed-history-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateFrameworkDbContext();
        await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
        var store = new PostgresEllaiSessionStore(db);
        await store.CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
            Actor.ActorId, "scope-test", null, null, now), default);
        db.Set<EllaiUtteranceEntity>().Add(new()
        {
            SessionId = id, ClientUtteranceId = "prior", Sequence = 1, UserText = "remember cobalt",
            AssistantText = "cobalt", Outcome = "Completed", AttemptCount = 1, AcceptedAtUtc = now,
            CompletedAtUtc = now, ToolTraceJson = EllaiToolTrace.SerializeFlattened([], false, [])
        });
        for (var index = 0; index < 105; index++)
            db.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = $"failed-{index}", Sequence = index + 2,
                UserText = "failed question", Outcome = "TerminalFailure", AcceptedAtUtc = now, CompletedAtUtc = now
            });
        db.Set<EllaiUtteranceEntity>().Add(new()
        {
            SessionId = id, ClientUtteranceId = "current", Sequence = 107,
            UserText = "what word?", Outcome = "Queued", AcceptedAtUtc = now
        });
        await db.SaveChangesAsync();

        var rows = await store.ReadConversationForExecutionAsync(id, Actor.ActorId, "current", default);
        Assert.Equal(new[] { "prior", "current" }, rows.Select(turn => turn.ClientUtteranceId));
        var history = await EllaiAuthorizedConversationContext.BuildAsync(rows, "current", Actor, null, default);
        Assert.Contains(history.Messages, message => message.Content == "cobalt");
        Assert.Empty(await store.ReadConversationForExecutionAsync(id, "foreign-owner", "current", default));
    }

    [FrameworkPostgresLiveMuseFact]
    public Task LiveMuseRecallsHistoryWithMoreThanOneHundredLaterTurnsQueued() =>
        VerifyAnchoredExecutionHistoryAsync(liveModel: true);

    private async Task VerifyAnchoredExecutionHistoryAsync(bool liveModel)
    {
        var id = $"framework-anchored-history-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        var work = new EllaiUtteranceWork(id, "scope-test", "What word?", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle-current", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "current");
        await using var db = CreateFrameworkDbContext();
        await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
        var store = new PostgresEllaiSessionStore(db);
        await store.CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 200,
            Actor.ActorId, "scope-test", null, null, now), default);
        db.Set<EllaiUtteranceEntity>().Add(new()
        {
            SessionId = id, ClientUtteranceId = "prior", Sequence = 1, UserText = "Remember cobalt",
            AssistantText = "I will remember cobalt.", Outcome = "Completed", AttemptCount = 1,
            AcceptedAtUtc = now, CompletedAtUtc = now,
            ToolTraceJson = EllaiToolTrace.SerializeFlattened([], false, [])
        });
        db.Set<EllaiUtteranceEntity>().Add(new()
        {
            SessionId = id, ClientUtteranceId = "current", Sequence = 2, UserText = "What word?",
            Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now,
            WorkPayloadJson = EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId)
        });
        for (var index = 0; index < 105; index++)
            db.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = $"later-{index}", Sequence = index + 3,
                UserText = "Later queued question", Outcome = "Queued", AcceptedAtUtc = now
            });
        await db.SaveChangesAsync();

        Assert.DoesNotContain(await store.ReadConversationAsync(id, Actor.ActorId, default), turn => turn.ClientUtteranceId == "current");
        var counter = new HistoryQueryCounter();
        await using var observedDb = CreateFrameworkDbContext(counter);
        var history = await new PostgresEllaiSessionStore(observedDb)
            .ReadConversationForExecutionAsync(id, Actor.ActorId, "current", default);
        Assert.Equal(1, counter.Count);
        Assert.Equal(new[] { "prior", "current" }, history.Select(turn => turn.ClientUtteranceId));
        var authorized = await EllaiAuthorizedConversationContext.BuildAsync(history, "current", Actor, null, default);
        Assert.Contains(authorized.Messages, message => message.Content.Contains("cobalt", StringComparison.Ordinal));
        Assert.Empty(await store.ReadConversationForExecutionAsync(id, "foreign-owner", "current", default));
        Assert.Empty(await store.ReadConversationForExecutionAsync(id, Actor.ActorId, "missing", default));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadConversationForExecutionAsync(id, Actor.ActorId, "current", cancelled.Token));
        }
        using var model = new RecoveryClient();
        using var live = liveModel ? EllaiMuseChatClientFactory.Create(EllaiOptions.Bind(Environment.GetEnvironmentVariable)) : null;
        await RunFrameworkWorkerAsync(id, "current", live ?? model, null, liveModel ? TimeSpan.FromMinutes(2) : null);
        if (liveModel)
        {
            var completed = (await store.ReadConversationForExecutionAsync(id, Actor.ActorId, "current", default))
                .Single(turn => turn.ClientUtteranceId == "current");
            Assert.Contains("cobalt", completed.AssistantText!, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("Remember cobalt", model.LastMessages);
            Assert.Contains("I will remember cobalt.", model.LastMessages);
            Assert.Contains("What word?", model.LastMessages);
        }
    }

    [FrameworkPostgresFact]
    public async Task ReceiptListHistoryRechecksPostgresAuthorityAfterStatusChanges()
    {
        var id = $"framework-receipt-history-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateFrameworkDbContext();
        await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
        _frameworkSessionIds.Add(id);
        await new PostgresEllaiSessionStore(db).CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, Actor.ActorId, "scope-test", null, null, now), default);
        var receipts = new PostgresEllaiApprovalStore(db);
        var receipt = await receipts.RecordAsync(id, "action", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 1,
            "synthetic-recipient", null, now, TimeSpan.FromHours(1), "policy", default);
        var consent = new EllaiConsentService();
        consent.Grant(id, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, receipts: receipts);
        var context = new EllaiToolContext(id, "scope-test", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: Actor,
            AllowedTools: new HashSet<string> { EllaiToolNames.Receipts });
        var request = new EllaiToolRequest(EllaiToolNames.Receipts, new Dictionary<string, string>());
        var result = await broker.InvokeAsync(context, request, default);
        Assert.True(result.Allowed);
        var lookup = new EllaiToolRequest(EllaiToolNames.Receipts,
            new Dictionary<string, string> { ["receipt"] = receipt.ReceiptId });
        var lookupResult = await broker.InvokeAsync(context, lookup, default);
        var lookupObservation = EllaiObservedToolResult.From(lookup, lookupResult);
        Assert.Equal(receipt.ReceiptId, lookupObservation.ReceiptLookupId);
        Assert.NotNull(lookupObservation.ReceiptListDigest);
        var trace = EllaiToolTrace.SerializeFlattened([EllaiObservedToolResult.From(request, result)], false, []);
        var lookupTrace = EllaiToolTrace.SerializeFlattened([lookupObservation], false, []);
        Assert.NotNull(trace);
        Assert.NotNull(lookupTrace);
        Assert.DoesNotContain("synthetic-recipient", trace);
        Assert.DoesNotContain("synthetic-recipient", lookupTrace);
        var turns = new[]
        {
            new EllaiConversationTurn(id, "prior", 1, "receipts?", "receipt answer", "Completed", 1, now) { ToolTraceJson = trace },
            new EllaiConversationTurn(id, "lookup", 2, "this receipt?", "lookup answer", "Completed", 1, now) { ToolTraceJson = lookupTrace },
            new EllaiConversationTurn(id, "current", 3, "what happened?", null, "Queued", 0, null)
        };
        var before = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor, null, default,
            toolContext: context, invokeTool: broker.InvokeAsync);
        Assert.Contains(before.Messages, message => message.Content == "receipt answer");
        Assert.Contains(before.Messages, message => message.Content == "lookup answer");
        await receipts.MarkStatusAsync(receipt.ReceiptId, EllaiReceiptStatus.Failed, DateTimeOffset.UtcNow, default);
        var after = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor, null, default,
            toolContext: context, invokeTool: broker.InvokeAsync);
        Assert.DoesNotContain(after.Messages, message => message.Content == "receipt answer");
        Assert.DoesNotContain(after.Messages, message => message.Content == "lookup answer");
    }

    [FrameworkPostgresFact]
    public async Task FrameworkWorkerRenewsPostgresClaimWhileModelIsRunning()
    {
        var id = $"framework-worker-lease-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, Actor.ActorId, "scope-test", null, null, now);
        var work = new EllaiUtteranceWork(id, "scope-test", "Synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "turn");
        await using (var db = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
            _frameworkSessionIds.Add(id);
            var store = new PostgresEllaiSessionStore(db);
            await store.CreateAsync(session, default);
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
                "turn", default, EllaiUtteranceWorkEnvelope.Serialize(work, "turn")));
        }
        var consent = new EllaiConsentService();
        using var model = new HeldRecoveryClient();
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateFrameworkDbContext());
        services.AddScoped<PostgresEllaiSessionStore>();
        services.AddScoped<IEllaiSessionStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUtteranceOutboxStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
        services.AddSingleton(consent).AddSingleton<EllaiUsageLedger>().AddSingleton(new EllaiSessionResponseStore());
        services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent));
        services.AddScoped(_ => new EllaiPromptBuilder("Synthetic policy", "fixture"));
        services.AddEllaiReasoningExecutor(new EllaiOptions
            { Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture" }, "framework");
        services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, model);
        await using var provider = services.BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(provider, NullLogger<EllaiUtteranceWorker>.Instance, TimeSpan.FromMilliseconds(100));
        await worker.StartAsync(default);
        try
        {
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            DateTimeOffset initial;
            await using (var db = CreateFrameworkDbContext())
                initial = (await db.Set<EllaiUtteranceEntity>().AsNoTracking().SingleAsync(item => item.SessionId == id)).LeaseExpiresAtUtc!.Value;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            DateTimeOffset extended;
            do
            {
                await Task.Delay(30, timeout.Token);
                await using var db = CreateFrameworkDbContext();
                extended = (await db.Set<EllaiUtteranceEntity>().AsNoTracking().SingleAsync(item => item.SessionId == id, timeout.Token)).LeaseExpiresAtUtc!.Value;
            } while (extended <= initial.AddMilliseconds(20));
            await using (var db = CreateFrameworkDbContext())
                Assert.Null(await new PostgresEllaiSessionStore(db).ClaimNextAsync(initial.AddMilliseconds(10), extended.AddMinutes(1), timeout.Token));
            model.Release.TrySetResult();
            while (true)
            {
                await Task.Delay(25, timeout.Token);
                await using var db = CreateFrameworkDbContext();
                var turn = Assert.Single(await new PostgresEllaiSessionStore(db).ReadConversationAsync(id, Actor.ActorId, timeout.Token));
                if (turn.CompletedAtUtc is null) continue;
                Assert.Equal("Completed", turn.Outcome);
                Assert.Equal(1, turn.AttemptCount);
                break;
            }
        }
        finally
        {
            model.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class HeldRecoveryClient : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic answer.","spokenText":"Synthetic answer.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [FrameworkPostgresFact]
    public async Task FrameworkWorkerPersistsFencedCaseResultSnapshot()
    {
        var id = $"framework-worker-snapshot-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        var work = new EllaiUtteranceWork(id, "scope-test", "Show my queue", "cases", null,
            EllaiMode.Ask, 1, "cycle", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.State },
            EllaiConsentPurposes.PublicProductHelp, "turn");
        await using (var setup = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(setup);
            var store = new PostgresEllaiSessionStore(setup);
            var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now);
            await store.CreateAsync(session, default);
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 },
                1, "turn", default, EllaiUtteranceWorkEnvelope.Serialize(work, "turn")));
        }

        using var model = new StateQueueClient();
        await RunFrameworkWorkerAsync(id, "turn", model, null, stateQueue: true);
        await using var check = CreateFrameworkDbContext();
        var topics = new EllaiTopicService(new PostgresEllaiTopicStore(check));
        var topic = Assert.Single(await new PostgresEllaiTopicStore(check).ListAsync(id, default));
        Assert.Equal(EllaiCaseResultMemory.TopicTitle, topic.Title);
        var recall = await new EllaiCaseResultMemory(topics, new EmptyStateQueue()).RecallAsync(
            new EllaiToolContext(id, "scope-test", "cases", null, 4, true, Actor: Actor), default);
        Assert.True(recall.Allowed);
        Assert.Equal(0, recall.ItemCount);
        Assert.Contains("empty", recall.ResultSummary, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyStateQueue : ICaseQueueQuery
    {
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) =>
            Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));

        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct) =>
            GetMineAsync(actor, limit, ct);
    }

    private sealed class StateQueueClient : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (++_calls == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("queue", EllaiToolNames.State,
                        new Dictionary<string, object?> { ["projection"] = "queue" })]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [FrameworkPostgresFact]
    public async Task FrameworkWorkerHoldsSessionFenceDuringCaseSnapshotWrite()
    {
        var id = $"framework-worker-snapshot-lock-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        var work = new EllaiUtteranceWork(id, "scope-test", "Show my queue", "cases", null,
            EllaiMode.Ask, 1, "cycle", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.State },
            EllaiConsentPurposes.PublicProductHelp, "turn");
        await using (var setup = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(setup);
            var store = new PostgresEllaiSessionStore(setup);
            var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now);
            await store.CreateAsync(session, default);
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 },
                1, "turn", default, EllaiUtteranceWorkEnvelope.Serialize(work, "turn")));
        }

        var hold = new HoldTopicInsert();
        using var model = new StateQueueClient();
        var running = RunFrameworkWorkerAsync(id, "turn", model, null, TimeSpan.FromSeconds(20),
            stateQueue: true, interceptor: hold);
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var cancelDb = CreateFrameworkDbContext();
            var canceller = new PostgresEllaiSessionStore(cancelDb);
            var session = (await canceller.GetAsync(id, default))!;
            var cancel = canceller.TryUpdateAsync(session with
                { SessionVersion = session.SessionVersion + 1, InteractionEpoch = session.InteractionEpoch + 1 },
                session.SessionVersion, default);
            await Task.Delay(200);
            Assert.False(cancel.IsCompleted);
            hold.Release.TrySetResult();
            Assert.NotNull(await cancel.WaitAsync(TimeSpan.FromSeconds(5)));
            await running.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            hold.Release.TrySetResult();
            try { await running; }
            catch (Exception) { /* Preserve the original assertion while ensuring the worker stops. */ }
        }
    }

    [FrameworkPostgresFact]
    public async Task FrameworkWorkerTimesOutStalledSnapshotAndProcessesNextTurn()
    {
        var id = $"framework-worker-snapshot-timeout-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        var work = new EllaiUtteranceWork(id, "scope-test", "Show my queue", "cases", null,
            EllaiMode.Ask, 1, "cycle", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.State },
            EllaiConsentPurposes.PublicProductHelp, "turn");
        await using (var setup = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(setup);
            var store = new PostgresEllaiSessionStore(setup);
            var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now);
            await store.CreateAsync(session, default);
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 },
                1, "turn", default, EllaiUtteranceWorkEnvelope.Serialize(work, "turn")));
        }

        var hold = new HoldTopicInsert(ignoreCancellation: true);
        using var model = new StateQueueClient();
        var running = RunFrameworkWorkerAsync(id, "turn", model, null, TimeSpan.FromSeconds(12),
            stateQueue: true, interceptor: hold, toolTimeout: TimeSpan.FromSeconds(2));
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(hold.Exited.Task.IsCompleted);

            await using (var nextDb = CreateFrameworkDbContext())
            {
                var store = new PostgresEllaiSessionStore(nextDb);
                var first = Assert.Single(await store.ReadConversationAsync(id, Actor.ActorId, default));
                Assert.NotEqual("Completed", first.Outcome);
                Assert.Null(first.AssistantText);
                var session = (await store.GetAsync(id, default))!;
                var next = work with { ClientUtteranceId = "next", CycleId = "next-cycle",
                    Utterance = "Hello", AllowedTools = new HashSet<string>() };
                Assert.NotNull(await store.TryAcceptUtteranceAsync(
                    session with { SessionVersion = session.SessionVersion + 1, CyclesUsed = session.CyclesUsed + 1 },
                    session.SessionVersion, "next", default, EllaiUtteranceWorkEnvelope.Serialize(next, "next")));
            }
            using var recovery = new RecoveryClient();
            await RunFrameworkWorkerAsync(id, "next", recovery, null, TimeSpan.FromSeconds(10));
            Assert.False(hold.Exited.Task.IsCompleted);
        }
        finally
        {
            hold.Release.TrySetResult();
            try { await running; } catch (Exception) { }
            if (hold.Entered.Task.IsCompleted) await hold.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await using var check = CreateFrameworkDbContext();
        Assert.Empty(await new PostgresEllaiTopicStore(check).ListAsync(id, default));
        var turns = await new PostgresEllaiSessionStore(check).ReadConversationAsync(id, Actor.ActorId, default);
        Assert.Equal("Completed", turns.Single(turn => turn.ClientUtteranceId == "next").Outcome);
    }

    [FrameworkPostgresFact]
    public async Task CaseResultSnapshotRejectsReclaimedOrCancelledAttempts()
    {
        var id = $"framework-snapshot-fence-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using (var setup = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(setup);
            var store = new PostgresEllaiSessionStore(setup);
            await store.CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now), default);
            setup.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = "turn", Sequence = 1, UserText = "synthetic",
                Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now, WorkPayloadJson = "{}"
            });
            await setup.SaveChangesAsync();
        }
        await using var firstDb = CreateFrameworkDbContext();
        await using var secondDb = CreateFrameworkDbContext();
        var first = new PostgresEllaiSessionStore(firstDb);
        var second = new PostgresEllaiSessionStore(secondDb);
        Assert.True(first.CanFenceTopicStore(new PostgresEllaiTopicStore(firstDb)));
        Assert.False(first.CanFenceTopicStore(new PostgresEllaiTopicStore(secondDb)));
        Assert.False(first.CanFenceTopicStore(new InMemoryEllaiTopicStore()));
        Assert.NotNull(await first.ClaimNextAsync(now, now.AddMinutes(2), default));
        Assert.False(await first.StoreCaseResultsIfCurrentAsync(id, "turn", 1, "other-actor",
            "scope-test", 1, "foreign", default));
        Assert.False(await first.StoreCaseResultsIfCurrentAsync(id, "turn", 1, Actor.ActorId,
            "other-scope", 1, "foreign", default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.StoreCaseResultsIfCurrentAsync(
            id, "turn", 1, Actor.ActorId, "scope-test", 1, "expired-deadline", default, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.True(await first.StoreCaseResultsIfCurrentAsync(id, "turn", 1, Actor.ActorId,
            "scope-test", 1, "first", default));
        await secondDb.Set<EllaiUtteranceEntity>().Where(row => row.SessionId == id)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseExpiresAtUtc, now.AddSeconds(-1)));
        Assert.False(await first.StoreCaseResultsIfCurrentAsync(id, "turn", 1, Actor.ActorId,
            "scope-test", 1, "expired", default));
        Assert.NotNull(await second.ClaimNextAsync(now, now.AddMinutes(2), default));
        Assert.False(await first.StoreCaseResultsIfCurrentAsync(id, "turn", 1, Actor.ActorId,
            "scope-test", 1, "stale", default));
        Assert.True(await second.StoreCaseResultsIfCurrentAsync(id, "turn", 2, Actor.ActorId,
            "scope-test", 1, "current", default));
        var session = (await first.GetAsync(id, default))!;
        Assert.NotNull(await first.TryUpdateAsync(session with
            { SessionVersion = session.SessionVersion + 1, InteractionEpoch = session.InteractionEpoch + 1 },
            session.SessionVersion, default));
        Assert.False(await second.StoreCaseResultsIfCurrentAsync(id, "turn", 2, Actor.ActorId,
            "scope-test", 1, "cancelled", default));
        await using var check = CreateFrameworkDbContext();
        var saved = Assert.Single(await new PostgresEllaiTopicStore(check).ListAsync(id, default));
        Assert.Equal("current", saved.EntityRef);
    }

    [FrameworkPostgresFact]
    public async Task CaseResultSnapshotHoldsSessionFenceUntilCommit()
    {
        var id = $"framework-snapshot-lock-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using (var setup = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(setup);
            var store = new PostgresEllaiSessionStore(setup);
            await store.CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now), default);
            setup.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = "turn", Sequence = 1, UserText = "synthetic",
                Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now, WorkPayloadJson = "{}"
            });
            await setup.SaveChangesAsync();
        }
        var hold = new HoldTopicInsert();
        await using var writerDb = CreateFrameworkDbContext(hold);
        await using var otherDb = CreateFrameworkDbContext();
        var writer = new PostgresEllaiSessionStore(writerDb);
        var other = new PostgresEllaiSessionStore(otherDb);
        Assert.NotNull(await writer.ClaimNextAsync(now, now.AddMinutes(2), default));
        var session = (await other.GetAsync(id, default))!;
        var write = writer.StoreCaseResultsIfCurrentAsync(id, "turn", 1, Actor.ActorId,
            "scope-test", 1, "snapshot", default);
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = other.TryUpdateAsync(session with
                { SessionVersion = session.SessionVersion + 1, InteractionEpoch = session.InteractionEpoch + 1 },
                session.SessionVersion, default);
            await Task.Delay(200);
            Assert.False(cancel.IsCompleted);
            hold.Release.TrySetResult();
            Assert.True(await write.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotNull(await cancel.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(await writer.StoreCaseResultsIfCurrentAsync(id, "turn", 1, Actor.ActorId,
                "scope-test", 1, "late", default));
        }
        finally { hold.Release.TrySetResult(); }
    }

    private sealed class HoldTopicInsert(bool ignoreCancellation = false) : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("ellai_topics", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                try
                {
                    if (ignoreCancellation) await Release.Task;
                    else await Release.Task.WaitAsync(cancellationToken);
                }
                finally { Exited.TrySetResult(); }
            }
            return result;
        }
    }

    [FrameworkPostgresFact]
    public async Task CaseResultSnapshotRollsBackWhenLeaseExpiresBeforeCommit()
    {
        var id = $"framework-snapshot-expiry-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using (var setup = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(setup);
            await new PostgresEllaiSessionStore(setup).CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now), default);
            setup.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = "turn", Sequence = 1, UserText = "synthetic",
                Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now, WorkPayloadJson = "{}"
            });
            await setup.SaveChangesAsync();
        }
        var hold = new HoldTopicInsert();
        await using var writerDb = CreateFrameworkDbContext(hold);
        var writer = new PostgresEllaiSessionStore(writerDb);
        var leaseStart = DateTimeOffset.UtcNow;
        Assert.NotNull(await writer.ClaimNextAsync(leaseStart, leaseStart.AddSeconds(2), default));
        var write = writer.StoreCaseResultsIfCurrentAsync(id, "turn", 1, Actor.ActorId,
            "scope-test", 1, "snapshot", default);
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(2.1));
        }
        finally { hold.Release.TrySetResult(); }
        Assert.False(await write.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var check = CreateFrameworkDbContext();
        Assert.Empty(await new PostgresEllaiTopicStore(check).ListAsync(id, default));
    }

    [FrameworkPostgresFact]
    public async Task SnapshotTransactionDeadlineReleasesSessionLockWhenClientStalls()
    {
        var id = $"framework-snapshot-deadline-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using (var setup = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(setup);
            await new PostgresEllaiSessionStore(setup).CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now), default);
            setup.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = "turn", Sequence = 1, UserText = "synthetic",
                Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now, WorkPayloadJson = "{}"
            });
            await setup.SaveChangesAsync();
        }
        var hold = new HoldTopicInsert();
        await using var writerDb = CreateFrameworkDbContext(hold);
        await using var otherDb = CreateFrameworkDbContext();
        var writer = new PostgresEllaiSessionStore(writerDb, TimeSpan.FromSeconds(2));
        var other = new PostgresEllaiSessionStore(otherDb);
        Assert.NotNull(await writer.ClaimNextAsync(now, now.AddMinutes(2), default));
        var session = (await other.GetAsync(id, default))!;
        var write = writer.StoreCaseResultsIfCurrentAsync(id, "turn", 1, Actor.ActorId,
            "scope-test", 1, "snapshot", default);
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var change = other.TryUpdateAsync(session with
                { SessionVersion = session.SessionVersion + 1, InteractionEpoch = session.InteractionEpoch + 1 },
                session.SessionVersion, default);
            await Task.Delay(200);
            Assert.False(change.IsCompleted);
            Assert.NotNull(await change.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.False(write.IsCompleted);
        }
        finally { hold.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var check = CreateFrameworkDbContext();
        Assert.Empty(await new PostgresEllaiTopicStore(check).ListAsync(id, default));
    }

    [FrameworkPostgresFact]
    public async Task PostgresLeaseRenewalProtectsCurrentAttemptAndRejectsReclaimedAttempt()
    {
        var id = $"framework-lease-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
            _frameworkSessionIds.Add(id);
            await new PostgresEllaiSessionStore(db).CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now), default);
            db.Set<EllaiUtteranceEntity>().Add(new EllaiUtteranceEntity
            {
                SessionId = id, ClientUtteranceId = "turn", Sequence = 1, UserText = "synthetic",
                Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now, WorkPayloadJson = "{}"
            });
            await db.SaveChangesAsync();
        }
        await using var firstDb = CreateFrameworkDbContext();
        await using var secondDb = CreateFrameworkDbContext();
        var first = new PostgresEllaiSessionStore(firstDb);
        var second = new PostgresEllaiSessionStore(secondDb);
        var initial = await first.ClaimNextAsync(now, now.AddSeconds(10), default);
        Assert.NotNull(initial);
        Assert.Equal(id, initial.SessionId);
        Assert.True(await first.RenewLeaseAsync(id, "turn", 1, now.AddSeconds(5), now.AddSeconds(20), default));
        Assert.Null(await second.ClaimNextAsync(now.AddSeconds(11), now.AddSeconds(30), default));
        var reclaimed = await second.ClaimNextAsync(now.AddSeconds(21), now.AddSeconds(31), default);
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.False(await first.RenewLeaseAsync(id, "turn", 1, now.AddSeconds(22), now.AddSeconds(40), default));
        Assert.True(await second.RenewLeaseAsync(id, "turn", 2, now.AddSeconds(22), now.AddSeconds(40), default));
    }

    [FrameworkPostgresFact]
    public async Task PostgresFeedRejectsAttemptReclaimedDuringRead()
    {
        var id = $"framework-feed-race-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using (var setup = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(setup);
            var store = new PostgresEllaiSessionStore(setup);
            await store.CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now), default);
            setup.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = "turn", Sequence = 1, UserText = "synthetic",
                Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now, WorkPayloadJson = "{}"
            });
            await setup.SaveChangesAsync();
        }

        await using var feedDb = CreateFrameworkDbContext();
        await using var reclaimDb = CreateFrameworkDbContext();
        var feedStore = new PostgresEllaiSessionStore(feedDb);
        var reclaimStore = new PostgresEllaiSessionStore(reclaimDb);
        Assert.NotNull(await feedStore.ClaimNextAsync(now, now.AddMinutes(1), default));
        var responses = new EllaiSessionResponseStore();
        var generation = responses.CaptureGeneration(id);
        Assert.True(responses.PublishProvisional(id, generation, "turn", 1, "scope-test", 1, 1, "private segment"));
        var outbox = new ReclaimAfterReadOutbox(feedStore, async () =>
            Assert.NotNull(await reclaimStore.ClaimNextAsync(now.AddMinutes(2), now.AddMinutes(3), default)));
        var result = await EllaiConversationFeed.ReadAsync(new DefaultHttpContext { User = Principal },
            id, 0, null, outbox, responses, feedStore, default);
        var page = (EllaiSessionResponsePageDto)((IValueHttpResult)result).Value!;
        Assert.DoesNotContain(page.Responses, item => item.Text == "private segment");
    }

    private sealed class ReclaimAfterReadOutbox(PostgresEllaiSessionStore inner, Func<Task> afterRead)
        : IEllaiUtteranceOutboxStore, IEllaiProvisionalDeliveryFence
    {
        public async Task<IReadOnlyList<EllaiConversationTurn>> ReadConversationAsync(string sessionId, string ownerActorId, CancellationToken ct)
        {
            var result = await inner.ReadConversationAsync(sessionId, ownerActorId, ct);
            await afterRead();
            return result;
        }
        public Task<bool> CanDeliverProvisionalAsync(string sessionId, string utteranceId, int attempt, string actorId,
            string scopeEpoch, long interactionEpoch, CancellationToken ct) =>
            inner.CanDeliverProvisionalAsync(sessionId, utteranceId, attempt, actorId, scopeEpoch, interactionEpoch, ct);
        public Task<EllaiConversationTurn?> ReadLatestGuidanceTurnAsync(string sessionId, string ownerActorId, CancellationToken ct) =>
            inner.ReadLatestGuidanceTurnAsync(sessionId, ownerActorId, ct);
        public Task<EllaiUtteranceOutboxItem?> ClaimNextAsync(DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct) =>
            inner.ClaimNextAsync(nowUtc, leaseExpiresAtUtc, ct);
        public Task<EllaiUtteranceResolution?> CompleteAsync(string sessionId, string clientUtteranceId, int attemptCount,
            DateTimeOffset completedAtUtc, CancellationToken ct, EllaiUtteranceResolution? resolution = null) =>
            inner.CompleteAsync(sessionId, clientUtteranceId, attemptCount, completedAtUtc, ct, resolution);
        public Task FailAsync(string sessionId, string clientUtteranceId, int attemptCount, DateTimeOffset retryAtUtc,
            bool terminal, CancellationToken ct) => inner.FailAsync(sessionId, clientUtteranceId, attemptCount, retryAtUtc, terminal, ct);
    }

    [FrameworkPostgresFact]
    public async Task ProvisionalPublicationHoldsSessionAndAttemptFence()
    {
        var id = $"framework-publication-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
            _frameworkSessionIds.Add(id);
            await new PostgresEllaiSessionStore(db).CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now), default);
            db.Set<EllaiUtteranceEntity>().Add(new EllaiUtteranceEntity
            {
                SessionId = id, ClientUtteranceId = "turn", Sequence = 1, UserText = "synthetic",
                Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now, WorkPayloadJson = "{}"
            });
            await db.SaveChangesAsync();
        }
        await using var publishDb = CreateFrameworkDbContext();
        await using var cancelDb = CreateFrameworkDbContext();
        var publisher = new PostgresEllaiSessionStore(publishDb);
        var canceller = new PostgresEllaiSessionStore(cancelDb);
        Assert.NotNull(await publisher.ClaimNextAsync(now, now.AddMinutes(5), default));
        var currentTurn = Assert.Single(await publisher.ReadConversationAsync(id, Actor.ActorId, default));
        Assert.Equal(1, currentTurn.AttemptCount);
        Assert.True(currentTurn.LeaseExpiresAtUtc > now);
        Assert.True(await publisher.CanDeliverProvisionalAsync(id, "turn", 1, Actor.ActorId, "scope-test", 1, default));
        Assert.False(await publisher.CanDeliverProvisionalAsync(id, "turn", 1, "another-actor", "scope-test", 1, default));
        Assert.False(await publisher.CanDeliverProvisionalAsync(id, "turn", 1, Actor.ActorId, "another-scope", 1, default));
        Assert.False(await publisher.PublishIfCurrentAsync(id, "turn", 1, "another-actor", "scope-test", 1,
            () => throw new InvalidOperationException("foreign publication"), default));
        Assert.False(await publisher.PublishIfCurrentAsync(id, "turn", 1, Actor.ActorId, "another-scope", 1,
            () => throw new InvalidOperationException("foreign scope publication"), default));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var publication = Task.Run(() => publisher.PublishIfCurrentAsync(id, "turn", 1, Actor.ActorId,
            "scope-test", 1, () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); return true; }, default));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var session = (await canceller.GetAsync(id, default))!;
            var cancel = canceller.TryUpdateAsync(session with
                { SessionVersion = session.SessionVersion + 1, InteractionEpoch = session.InteractionEpoch + 1 }, session.SessionVersion, default);
            await Task.Delay(200);
            Assert.False(cancel.IsCompleted);
            release.Set();
            Assert.True(await publication);
            Assert.NotNull(await cancel);
            Assert.False(await publisher.CanDeliverProvisionalAsync(id, "turn", 1, Actor.ActorId, "scope-test", 1, default));
            Assert.False(await publisher.PublishIfCurrentAsync(id, "turn", 1, Actor.ActorId, "scope-test", 1,
                () => throw new InvalidOperationException("stale publication"), default));
            await cancelDb.Set<EllaiUtteranceEntity>().Where(turn => turn.SessionId == id && turn.ClientUtteranceId == "turn")
                .ExecuteUpdateAsync(update => update.SetProperty(turn => turn.AttemptCount, 2));
            Assert.False(await publisher.PublishIfCurrentAsync(id, "turn", 1, Actor.ActorId, "scope-test", 2,
                () => throw new InvalidOperationException("reclaimed publication"), default));
            Assert.False(await publisher.CanDeliverProvisionalAsync(id, "turn", 1, Actor.ActorId, "scope-test", 2, default));
            Assert.True(await publisher.CanDeliverProvisionalAsync(id, "turn", 2, Actor.ActorId, "scope-test", 2, default));
            Assert.True(await publisher.PublishIfCurrentAsync(id, "turn", 2, Actor.ActorId, "scope-test", 2,
                () => true, default));
            Assert.NotNull(await publisher.CompleteAsync(id, "turn", 2, DateTimeOffset.UtcNow, default,
                EllaiUtteranceResolution.Completed("synthetic", 2)));
            Assert.True(await publisher.CanDeliverProvisionalAsync(id, "turn", 2, Actor.ActorId, "scope-test", 2, default));
        }
        finally { release.Set(); }
    }

    [FrameworkPostgresFact]
    public async Task PostgresRoundTripsLargeProvenanceWithoutAuthorizingUnknownEvidence()
    {
        var id = $"framework-large-trace-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var observations = Enumerable.Range(0, 300).Select(index => new EllaiObservedToolResult(
            EllaiToolNames.Now, true, EllaiToolReasonCodes.Ok,
            [new EvidenceRef("fixture", index.ToString(), "v1", null, "proof")], false)).ToArray();
        var trace = EllaiToolTrace.SerializeFlattened(observations, false, []);
        Assert.NotNull(trace);
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateFrameworkDbContext())
        {
            await new PostgresEllaiSessionStore(db).CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
                0, 12, Actor.ActorId, "scope-test", null, null, now), default);
            db.Set<EllaiUtteranceEntity>().AddRange(
                new EllaiUtteranceEntity { SessionId = id, ClientUtteranceId = "prior", Sequence = 1,
                    UserText = "question", AssistantText = "answer", Outcome = "Completed", AttemptCount = 1,
                    AcceptedAtUtc = now, CompletedAtUtc = now, ToolTraceJson = trace },
                new EllaiUtteranceEntity { SessionId = id, ClientUtteranceId = "current", Sequence = 2,
                    UserText = "follow-up", Outcome = "Queued", AttemptCount = 0, AcceptedAtUtc = now });
            await db.SaveChangesAsync();
        }
        await using var restoredDb = CreateFrameworkDbContext();
        var store = new PostgresEllaiSessionStore(restoredDb);
        var turns = await store.ReadConversationAsync(id, Actor.ActorId, default);
        Assert.True(EllaiToolTrace.TryRead(turns[0].ToolTraceJson!, out var restored));
        Assert.Equal(300, restored!.Observations.Count);
        var history = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor, null, default);
        Assert.DoesNotContain(history.Messages, message => message.Content == "answer");
        Assert.Empty(await store.ReadConversationAsync(id, "different-owner", default));
    }

    [FrameworkPostgresFact]
    public async Task FrameworkWorkerRestoresHistoryAfterManyRepeatedToolObservations()
    {
        var id = $"framework-long-history-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, Actor.ActorId, "scope-test", null, null, DateTimeOffset.UtcNow);
        var first = new EllaiUtteranceWork(id, "scope-test", "First synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle-1", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.Now },
            EllaiConsentPurposes.PublicProductHelp, "turn-1");
        await using (var db = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
            var store = new PostgresEllaiSessionStore(db);
            await store.CreateAsync(session, default);
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
                first.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(first, first.ClientUtteranceId)));
        }
        using var model = new LongRecoveryClient();
        await RunFrameworkWorkerAsync(id, "turn-1", model, null, TimeSpan.FromSeconds(30));
        Assert.Equal(300, model.ToolResults);
        await using (var db = CreateFrameworkDbContext())
        {
            var store = new PostgresEllaiSessionStore(db);
            var turns = await store.ReadConversationAsync(id, Actor.ActorId, default);
            Assert.Equal("Completed", Assert.Single(turns).Outcome);
            Assert.True(EllaiToolTrace.TryRead(turns[0].ToolTraceJson!, out var trace));
            Assert.Equal(EllaiObservedToolResult.CurrentClockVersion, Assert.Single(trace!.Observations).ClockVersion);
            var current = (await store.GetAsync(id, default))!;
            var next = first with { Utterance = "Second synthetic question", ClientUtteranceId = "turn-2", CycleId = "cycle-2" };
            Assert.NotNull(await store.TryAcceptUtteranceAsync(current with
                { SessionVersion = current.SessionVersion + 1, CyclesUsed = current.CyclesUsed + 1 }, current.SessionVersion,
                next.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(next, next.ClientUtteranceId)));
        }
        await RunFrameworkWorkerAsync(id, "turn-2", model, null);
        Assert.Contains("First synthetic question", model.LastMessages);
        Assert.Contains("Synthetic recovered answer.", model.LastMessages);
        var third = first with { Utterance = "Third synthetic question", ClientUtteranceId = "turn-3", CycleId = "cycle-3",
            AllowedTools = new HashSet<string>() };
        await using (var db = CreateFrameworkDbContext())
        {
            var store = new PostgresEllaiSessionStore(db);
            var current = (await store.GetAsync(id, default))!;
            Assert.NotNull(await store.TryAcceptUtteranceAsync(current with
                { SessionVersion = current.SessionVersion + 1, CyclesUsed = current.CyclesUsed + 1 }, current.SessionVersion,
                third.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(third, third.ClientUtteranceId)));
        }
        await RunFrameworkWorkerAsync(id, "turn-3", model, null);
        Assert.Contains("Third synthetic question", model.LastMessages);
        Assert.DoesNotContain("First synthetic question", model.LastMessages);
        Assert.DoesNotContain("Second synthetic question", model.LastMessages);
        Assert.DoesNotContain("Synthetic recovered answer.", model.LastMessages);
    }

    private sealed class LongRecoveryClient : IChatClient
    {
        private int _calls;
        public IReadOnlyList<string> LastMessages { get; private set; } = [];
        public int ToolResults { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ToolResults = Math.Max(ToolResults, messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
            LastMessages = messages.Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
                .Select(message => message.Text).ToArray();
            if (++_calls == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant, Enumerable.Range(0, 300)
                    .Select(index => (AIContent)new FunctionCallContent($"clock-{index}", EllaiToolNames.Now,
                        new Dictionary<string, object?>())).ToList());
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic recovered answer.","spokenText":"Synthetic recovered answer.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 4 })]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [FrameworkPostgresFact]
    public async Task FrameworkUsageEstimatesSurvivePostgresRecoveryWithoutInflatingReportedSpend()
    {
        var id = $"framework-usage-estimate-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        await using (var db = CreateFrameworkDbContext())
        {
            await new PostgresEllaiUsageStore(db).RecordAsync(new StoredUsageSample(id, 10, 5, 0.01m, DateTimeOffset.UtcNow)
            {
                EstimatedPromptTokens = 100,
                EstimatedCompletionTokens = 200,
                EstimatedDollars = 0.5m
            }, default);
        }
        await using var restoredDb = CreateFrameworkDbContext();
        var store = new PostgresEllaiUsageStore(restoredDb);
        var totals = await store.TotalsAsync(id, default);
        Assert.Equal(10, totals.PromptTokens);
        Assert.Equal(5, totals.CompletionTokens);
        Assert.Equal(315, totals.TotalTokens);
        Assert.Equal(0.01m, await store.SpendForAsync(id, default));
        Assert.Equal(0.51m, await store.BudgetSpendForAsync(id, default));
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 400, envelopeSpendCap: 1m,
            prices: new EllaiPriceTable(1m, 1m, 0m));
        await ledger.HydrateAsync(id, store, default);
        Assert.Equal(315, ledger.TotalsFor(id).TotalTokens);
        Assert.Equal(0.01m, ledger.SpendFor(id));
        using var reservation = ledger.TryBeginModelCall(id, 100);
        Assert.Null(reservation);
    }

    [FrameworkPostgresFact]
    public async Task FrameworkUsageRejectsNegativeEstimatesInPostgres()
    {
        await using var db = CreateFrameworkDbContext();
        db.Set<EllaiUsageSampleEntity>().Add(new EllaiUsageSampleEntity
        {
            SampleId = $"invalid-{Guid.NewGuid():N}", SessionId = "framework-invalid",
            AtUtc = DateTimeOffset.UtcNow, EstimatedPromptTokens = -1
        });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [FrameworkPostgresFact]
    public async Task FrameworkUsageHydrationSaturatesLargePostgresTotals()
    {
        var id = $"framework-usage-overflow-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        await using (var db = CreateFrameworkDbContext())
        {
            var store = new PostgresEllaiUsageStore(db);
            await store.RecordAsync(new StoredUsageSample(id, int.MaxValue, int.MaxValue, 0, DateTimeOffset.UtcNow), default);
            await store.RecordAsync(new StoredUsageSample(id, 1, 1, 0, DateTimeOffset.UtcNow), default);
        }
        await using var restoredDb = CreateFrameworkDbContext();
        var ledger = new EllaiUsageLedger();
        await ledger.HydrateAsync(id, new PostgresEllaiUsageStore(restoredDb), default);
        Assert.Equal(int.MaxValue, ledger.TotalsFor(id).TotalTokens);
        Assert.False(ledger.TryReserve(id, 1));
    }

    [FrameworkPostgresFact]
    public Task FrameworkWorkerRehydratesOwnedConversationAfterWorkerRestart() => VerifyFrameworkRecoveryAsync(null);

    [FrameworkPostgresRedisDownFact]
    public async Task FrameworkWorkerRecoversPostgresConversationWhileRedisIsUnavailable()
    {
        var connection = Environment.GetEnvironmentVariable("SWIFTAID_TEST_REDIS_DOWN_CONNECTION")!;
        var parsed = ConfigurationOptions.Parse(connection);
        var safeEndpoint = parsed.EndPoints.Count == 1 && (parsed.EndPoints[0] switch
        {
            IPEndPoint ip => IPAddress.IsLoopback(ip.Address) && ip.Port is >= 1024 and <= 65535,
            DnsEndPoint dns => dns.Host == "localhost" && dns.Port is >= 1024 and <= 65535,
            _ => false
        });
        if (!safeEndpoint)
            throw new InvalidOperationException("Redis outage fixture requires one disposable loopback endpoint.");
        using var cache = new RedisCache(Options.Create(new RedisCacheOptions
        {
            Configuration = connection, InstanceName = "swiftaid:framework-recovery-fixture:"
        }));
        var store = new RedisCacheStore(cache, Options.Create(new RedisOptions { MaximumTtlSeconds = 60 }),
            NullLogger<RedisCacheStore>.Instance);
        Assert.Equal(CacheReadStatus.Failed, (await store.GetAsync("fixture", default)).Status);
        await VerifyFrameworkRecoveryAsync(store);
        Assert.Equal(CacheReadStatus.Failed, (await store.GetAsync("fixture", default)).Status);
    }

    private async Task VerifyFrameworkRecoveryAsync(ICacheStore? cache)
    {
        var id = $"framework-recovery-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
            Actor.ActorId, "scope-test", null, null, DateTimeOffset.UtcNow);
        var first = new EllaiUtteranceWork(id, "scope-test", "First synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle-1", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "turn-1");
        await using (var db = CreateFrameworkDbContext())
        {
            await CaseAuthorityPostgresFixture.MigrateFrameworkDatabaseAsync(db);
            var store = new PostgresEllaiSessionStore(db);
            await store.CreateAsync(session, default);
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
                first.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(first, first.ClientUtteranceId)));
        }

        using var model = new RecoveryClient();
        await RunFrameworkWorkerAsync(id, "turn-1", model, cache);
        await using (var db = CreateFrameworkDbContext())
        {
            var checkpoint = await new PostgresEllaiSessionStore(db)
                .ReadFrameworkSessionAsync(id, Actor.ActorId, "turn-1", default);
            Assert.False(string.IsNullOrWhiteSpace(checkpoint));
            Assert.DoesNotContain("First synthetic question", checkpoint, StringComparison.Ordinal);
        }
        var second = first with { Utterance = "Second synthetic question", ClientUtteranceId = "turn-2", CycleId = "cycle-2" };
        await using (var db = CreateFrameworkDbContext())
        {
            var store = new PostgresEllaiSessionStore(db);
            var current = (await store.GetAsync(id, default))!;
            Assert.NotNull(await store.TryAcceptUtteranceAsync(current with
                { SessionVersion = current.SessionVersion + 1, CyclesUsed = current.CyclesUsed + 1 }, current.SessionVersion,
                second.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(second, second.ClientUtteranceId)));
        }
        await RunFrameworkWorkerAsync(id, "turn-2", model, cache);

        await using var verifyDb = CreateFrameworkDbContext();
        var verified = new PostgresEllaiSessionStore(verifyDb);
        var turns = await verified.ReadConversationAsync(id, Actor.ActorId, default);
        Assert.Equal(2, turns.Count);
        Assert.All(turns, turn => Assert.Equal("Completed", turn.Outcome));
        var executionTurns = await verified.ReadConversationForExecutionAsync(id, Actor.ActorId, "turn-2", default);
        foreach (var executionTurn in executionTurns)
            Assert.False(string.IsNullOrWhiteSpace(await verified.ReadFrameworkSessionAsync(id, Actor.ActorId, executionTurn.ClientUtteranceId, default)));
        Assert.Empty(await verified.ReadConversationAsync(id, "different-owner", default));
        Assert.Contains("First synthetic question", model.LastMessages);
        Assert.Contains("Synthetic recovered answer.", model.LastMessages);
        Assert.Contains("Second synthetic question", model.LastMessages);
        Assert.Equal(2, (await new PostgresEllaiUsageStore(verifyDb).TotalsAsync(id, default)).Calls);
        Assert.Equal(0.000026m, await new PostgresEllaiUsageStore(verifyDb).SpendForAsync(id, default));
    }

    private static async Task RunFrameworkWorkerAsync(string sessionId, string turnId, IChatClient model, ICacheStore? cache,
        TimeSpan? executionTimeout = null, bool stateQueue = false, DbCommandInterceptor? interceptor = null,
        TimeSpan? toolTimeout = null, TimeSpan? snapshotTransactionLimit = null)
    {
        var consent = new EllaiConsentService();
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateFrameworkDbContext(interceptor));
        services.AddScoped(sp => new PostgresEllaiSessionStore(sp.GetRequiredService<SwiftAidDbContext>(), snapshotTransactionLimit));
        services.AddScoped<IEllaiSessionStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUtteranceOutboxStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
        services.AddSingleton(consent);
        if (cache is not null) services.AddSingleton(cache);
        services.AddEllaiUsageAccounting(new EllaiOptions { SpendingCapDollars = 70m,
            PricingInputPer1KTokens = 0.001m, PricingOutputPer1KTokens = 0.002m });
        services.AddSingleton(new EllaiSessionResponseStore());
        if (stateQueue)
        {
            services.AddScoped<ICaseQueueQuery>(_ => new EmptyStateQueue());
            services.AddScoped<IEllaiTopicStore, PostgresEllaiTopicStore>();
            services.AddScoped<EllaiTopicService>();
            services.AddScoped(sp => new EllaiToolBroker(new EllaiPolicyService(), consent,
                queue: sp.GetRequiredService<ICaseQueueQuery>(), topics: sp.GetRequiredService<EllaiTopicService>()));
        }
        else
            services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent));
        services.AddScoped(_ => new EllaiPromptBuilder("Synthetic test policy", "fixture"));
        services.AddEllaiReasoningExecutor(new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture"
        }, "framework");
        services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, model);
        if (toolTimeout is not null)
            services.AddScoped<IEllaiReasoningExecutor>(sp => new EllaiFrameworkExecutor(
                model, sp.GetRequiredService<EllaiPromptBuilder>(), sp.GetRequiredService<EllaiToolBroker>(),
                sp.GetRequiredService<EllaiUsageLedger>(), sp.GetRequiredService<IEllaiUsageStore>(), toolTimeout));
        await using var provider = services.BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(provider, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(executionTimeout ?? TimeSpan.FromSeconds(10));
            while (true)
            {
                await Task.Delay(25, timeout.Token);
                await using var db = CreateFrameworkDbContext();
                var turns = await new PostgresEllaiSessionStore(db).ReadConversationForExecutionAsync(sessionId, Actor.ActorId, turnId, timeout.Token);
                if (turns.Single(turn => turn.ClientUtteranceId == turnId).CompletedAtUtc is not null) break;
            }
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class RecoveryClient : IChatClient
    {
        public IReadOnlyList<string> LastMessages { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            LastMessages = messages.Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
                .Select(message => message.Text).ToArray();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic recovered answer.","spokenText":"Synthetic recovered answer.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 4 })]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "ellai-pg-test-actor"),
            new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
    private static ActorContext Actor => ActorContext.FromValidatedPrincipal(Principal);
    private readonly List<string> _sessionIds = new();
    private readonly List<string> _frameworkSessionIds = new();

    public Task InitializeAsync() => Task.CompletedTask;

    [FrameworkPostgresFact]
    public async Task FrameworkCheckpointCommitsWithFencedTurnAndSurvivesNewContext()
    {
        var id = $"framework-checkpoint-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        await using var firstDb = CreateFrameworkDbContext();
        await firstDb.Database.MigrateAsync();
        var first = new PostgresEllaiSessionStore(firstDb);
        var session = new EllaiSessionRecord(id, EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, Actor.ActorId, "scope-test", null, null, now);
        await first.CreateAsync(session, default);
        Assert.NotNull(await first.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 },
            1, "turn-1", default, "{\"utterance\":\"question\"}"));
        var original = (await first.ClaimNextAsync(now, now.AddSeconds(1), default))!;
        await using (var blockerDb = CreateFrameworkDbContext())
        await using (var blocker = await blockerDb.Database.BeginTransactionAsync())
        {
            await blockerDb.Set<EllaiUtteranceEntity>().FromSqlInterpolated($"""
                SELECT * FROM ellai_utterances WHERE session_id = {id} AND client_utterance_id = 'turn-1' FOR UPDATE
                """).AsNoTracking().ToListAsync();
            var late = first.CompleteAsync(id, "turn-1", original.AttemptCount, now, default,
                EllaiUtteranceResolution.Completed("expired", 1) with { FrameworkSessionJson = "{\"attempt\":0}" });
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(late.IsCompleted);
            await blocker.CommitAsync();
            Assert.Null(await late);
        }
        await using var secondDb = CreateFrameworkDbContext();
        var second = new PostgresEllaiSessionStore(secondDb);
        var retry = (await second.ClaimNextAsync(now.AddSeconds(2), now.AddMinutes(1), default))!;
        Assert.Null(await first.CompleteAsync(id, "turn-1", original.AttemptCount, now, default,
            EllaiUtteranceResolution.Completed("stale", 1) with { FrameworkSessionJson = "{\"attempt\":1}" }));
        Assert.NotNull(await second.CompleteAsync(id, "turn-1", retry.AttemptCount, now, default,
            EllaiUtteranceResolution.Completed("answer", 1) with { FrameworkSessionJson = "{\"attempt\":2}" }));

        await using var restoredDb = CreateFrameworkDbContext();
        var restored = new PostgresEllaiSessionStore(restoredDb);
        var checkpoint = await restored.ReadFrameworkSessionAsync(id, Actor.ActorId, "turn-1", default);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(checkpoint!).RootElement.GetProperty("attempt").GetInt32());
        Assert.Null(await restored.ReadFrameworkSessionAsync(id, "foreign-owner", "turn-1", default));
        Assert.Empty(await restored.ReadConversationForExecutionAsync(id, "foreign-owner", "turn-1", default));

        var current = (await restored.GetAsync(id, default))!;
        Assert.NotNull(await restored.TryAcceptUtteranceAsync(current with { SessionVersion = current.SessionVersion + 1,
                CyclesUsed = current.CyclesUsed + 1 },
            current.SessionVersion, "turn-2", default, "{\"utterance\":\"next\"}"));
        var currentClaim = (await restored.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default))!;
        current = (await restored.GetAsync(id, default))!;
        Assert.NotNull(await restored.TryUpdateAsync(current with { SessionVersion = current.SessionVersion + 1,
            ScopeEpoch = "changed-scope" }, current.SessionVersion, default));
        var denied = await restored.CompleteAsync(id, "turn-2", currentClaim.AttemptCount, DateTimeOffset.UtcNow, default,
            EllaiUtteranceResolution.Completed("answer", current.InteractionEpoch) with
            {
                ExpectedOwnerActorId = Actor.ActorId,
                ExpectedScopeEpoch = "scope-test",
                FrameworkSessionJson = "{\"attempt\":3}"
            });
        Assert.Equal("Cancelled", denied!.Outcome);
        Assert.Null(denied.FrameworkSessionJson);
    }

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
        if (Enabled() && _sessionIds.Count > 0)
        {
            await using var db = CreateDbContext();
            await DeleteSessionsAsync(db, _sessionIds);
        }
        if (_frameworkSessionIds.Count > 0)
        {
            await using var db = CreateFrameworkDbContext();
            await DeleteSessionsAsync(db, _frameworkSessionIds);
        }
    }

    private static async Task DeleteSessionsAsync(SwiftAidDbContext db, IEnumerable<string> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            await db.Set<EllaiTopicEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
            await db.Set<EllaiUsageSampleEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
            await db.Set<EllaiConsentGrantEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
            await db.Set<EllaiUtteranceEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
            await db.Set<EllaiCheckpointEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
            await db.Set<EllaiPlanApprovalEntity>().Where(e => e.SessionId == id).ExecuteDeleteAsync();
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

    private sealed class HistoryQueryCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static SwiftAidDbContext CreateFrameworkDbContext(DbCommandInterceptor? interceptor = null)
    {
        var value = Environment.GetEnvironmentVariable("SWIFTAID_FRAMEWORK_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("The disposable framework database is required.");
        var builder = new NpgsqlConnectionStringBuilder(value);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || builder.Port is < 1024 or > 65535 ||
            builder.Database != "swiftaid_framework_fixture" || builder.Username != "p21_migration_owner" || string.IsNullOrWhiteSpace(builder.Password))
            throw new InvalidOperationException("The framework fixture must use its named disposable loopback database.");
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(builder.ConnectionString).UseOpenIddict();
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new SwiftAidDbContext(options.Options);
    }

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
