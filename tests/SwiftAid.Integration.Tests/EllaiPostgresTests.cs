using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
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
    public Task FrameworkWorkerPersistsGuidanceAfterUnmatchedCatalogueGoal() => VerifyReportGuidanceAsync(false);

    [FrameworkPostgresLiveMuseFact]
    public Task LiveMusePersistsReportGuidance() => VerifyReportGuidanceAsync(true);

    [FrameworkPostgresLiveMuseFact]
    public Task LiveMusePlansPersistedReportGuidance() => VerifyReportGuidanceAsync(true, planGuidance: true);

    private async Task VerifyReportGuidanceAsync(bool liveModel, bool planGuidance = false)
    {
        var id = $"framework-fallback-guidance-{Guid.NewGuid():N}";
        _frameworkSessionIds.Add(id);
        var now = DateTimeOffset.UtcNow;
        var work = new EllaiUtteranceWork(id, "scope-test", "Show me how to save a report draft.", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle-current", Actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.UiCatalogue },
            EllaiConsentPurposes.PublicProductHelp, "current");
        await using (var db = CreateFrameworkDbContext())
        {
            await db.Database.MigrateAsync();
            await new PostgresEllaiSessionStore(db).CreateAsync(new(id, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
                Actor.ActorId, "scope-test", null, null, now), default);
            db.Set<EllaiUtteranceEntity>().Add(new()
            {
                SessionId = id, ClientUtteranceId = "current", Sequence = 1, UserText = work.Utterance,
                Outcome = "Queued", AcceptedAtUtc = now, WorkPayloadJson = EllaiUtteranceWorkEnvelope.Serialize(work, "current")
            });
            await db.SaveChangesAsync();
        }
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        using var model = new FallbackGuidanceClient();
        using var live = liveModel ? EllaiMuseChatClientFactory.Create(EllaiOptions.Bind(Environment.GetEnvironmentVariable)) : null;
        await RunFrameworkWorkerAsync(id, "current", live ?? model, null,
            executionTimeout: liveModel ? TimeSpan.FromMinutes(2) : null, registry: registry);
        await using var verifyDb = CreateFrameworkDbContext();
        var completed = (await new PostgresEllaiSessionStore(verifyDb).ReadConversationForExecutionAsync(id, Actor.ActorId, "current", default)).Single();
        Assert.Equal("Completed", completed.Outcome);
        Assert.NotNull(completed.UiProposalsJson);
        var proposal = Assert.Single(EllaiUiProposalPayload.Deserialize(completed.UiProposalsJson));
        Assert.Equal(UiIntentKind.StartGuidance, proposal.Kind);
        Assert.Equal("report", proposal.Arguments["feature"]);
        Assert.True(EllaiToolTrace.TryRead(completed.ToolTraceJson!, out var trace));
        Assert.Contains(trace!.Observations, observation => observation.ToolName == EllaiToolNames.UiCatalogue &&
            observation.UiCatalogueReplayScope is not null);
        Assert.DoesNotContain("synthetic unfamiliar goal", completed.ToolTraceJson!);
        if (planGuidance)
        {
            var projected = EllaiConversationProjection.ToClient(completed);
            var proposalId = Assert.Single(projected.UiProposals).ProposalId;
            var observations = new InMemoryEllaiUiObservationStore();
            await observations.RecordAsync(new(id, "view", "context", "web", ["report.entry"], DateTimeOffset.UtcNow,
                [new("report.entry", UiObservationState.Ready)]), default);
            var store = new PostgresEllaiSessionStore(verifyDb);
            var admission = new EllaiGuidanceProposalAdmission(store, store, registry, observations);
            var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var router = new EllaiReasoningRouter(options, new EllaiReasoningAdapter(http, options),
                new OllamaEllaiReasoningProvider(http, options), new ServerlessEllaiReasoningProvider(http, options));
            var planner = new EllaiModelGuidancePlanner(new EllaiProviderTurnClient(router));
            var authority = new EllaiUiEntitlements(Actor.ActorId, "scope-test", new HashSet<string> { "report" }, new HashSet<string>());
            var plan = await admission.PlanAsync(id, proposalId, authority, planner, new EllaiUiDiscoveryService(),
                new EllaiGuidancePlanValidator(registry), new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), default);
            Assert.Equal("Ready", plan.Status);
            Assert.NotEmpty(plan.Plan!.Steps);
            Assert.NotNull(EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync(id, default))!.GuidanceJson)?.Plan);
        }
    }

    private sealed class FallbackGuidanceClient : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (++_calls == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("discover", EllaiToolNames.UiCatalogue,
                        new Dictionary<string, object?> { ["feature"] = "report", ["goal"] = "synthetic unfamiliar goal" })]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Let me show you the report form.","spokenText":"Let me show you the report form.","claims":[],"questions":[],"uiIntents":[{"kind":"StartGuidance","featureId":"ellai.guidance","resourceId":null,"controlId":null,"arguments":{"feature":"report","objective":"Open the report form"}}],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 4 })]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [FrameworkPostgresFact]
    public async Task PartialCaseConversationRevalidatesAfterPostgresTopicIsReopened()
    {
        var sessionId = $"partial-history-{Guid.NewGuid():N}";
        await using var initDb = CreateFrameworkDbContext();
        await initDb.Database.MigrateAsync();
        var caseId = Guid.NewGuid();
        var queue = new MutableHistoryQueue { Rows = [caseId] };
        var consent = new EllaiConsentService();
        consent.Grant(sessionId, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var context = new EllaiToolContext(sessionId, "scope-test", "cases", null, 4, true,
            Actor: Actor, DataClassification: EllaiProcessingClassification.SyntheticDemo,
            AllowedTools: new HashSet<string> { EllaiToolNames.Evidence });
        var request = new EllaiToolRequest(EllaiToolNames.Evidence,
            new Dictionary<string, string> { ["ref"] = "case-results" });
        try
        {
            string? trace;
            await using (var db = CreateFrameworkDbContext())
            {
                var topics = new EllaiTopicService(new PostgresEllaiTopicStore(db));
                var row = new SwiftAid.Contracts.Cases.CaseQueueItem(caseId, "Reported", "SZ", Actor.ActorId, 1, DateTimeOffset.UtcNow);
                await new EllaiCaseResultMemory(topics, queue).RememberAsync(context, [row], default, searchIncomplete: true);
                var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue, topics: topics);
                var result = await broker.InvokeAsync(context, request, default);
                Assert.True(result.Truncated);
                trace = EllaiToolTrace.SerializeFlattened([EllaiObservedToolResult.From(request, result)], false, []);
                Assert.NotNull(trace);
            }
            var turns = new[]
            {
                new EllaiConversationTurn(sessionId, "prior", 1, "cases?", "partial result", "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace },
                new EllaiConversationTurn(sessionId, "current", 2, "continue", null, "Queued", 1, null)
            };
            await using (var db = CreateFrameworkDbContext())
            {
                var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue,
                    topics: new EllaiTopicService(new PostgresEllaiTopicStore(db)));
                async Task<bool> Retained() => (await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor,
                    queue, default, toolContext: context, invokeTool: broker.InvokeAsync)).Messages.Any(message => message.Content == "partial result");
                Assert.True(await Retained());
                queue.Rows = [];
                Assert.False(await Retained());
            }
        }
        finally
        {
            await using var cleanupDb = CreateFrameworkDbContext();
            await cleanupDb.Set<EllaiTopicEntity>().Where(topic => topic.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }

    [FrameworkPostgresFact]
    public async Task CaseResultToolsRestoreOriginalAfterNewSearchAndRecheckAccess()
    {
        var sessionId = $"case-history-tools-{Guid.NewGuid():N}";
        await using var initDb = CreateFrameworkDbContext();
        await initDb.Database.MigrateAsync();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var queue = new MutableHistoryQueue();
        var consent = new EllaiConsentService();
        consent.Grant(sessionId, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var context = new EllaiToolContext(sessionId, "scope-test", "cases", null, 4, true,
            Actor: Actor, DataClassification: EllaiProcessingClassification.SyntheticDemo);
        var state = new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string> { ["projection"] = "queue" });
        var originalRequest = new EllaiToolRequest(EllaiToolNames.Evidence, new Dictionary<string, string> { ["ref"] = "case-results-original" });
        var latestRequest = new EllaiToolRequest(EllaiToolNames.Evidence, new Dictionary<string, string> { ["ref"] = "case-results" });
        try
        {
            queue.Rows = [firstId];
            await using (var db = CreateFrameworkDbContext())
            {
                var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue,
                    topics: new EllaiTopicService(new PostgresEllaiTopicStore(db)));
                Assert.True((await broker.InvokeAsync(context, state, default)).Allowed);
            }
            queue.Rows = [secondId];
            await using (var db = CreateFrameworkDbContext())
            {
                var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue,
                    topics: new EllaiTopicService(new PostgresEllaiTopicStore(db)));
                Assert.True((await broker.InvokeAsync(context, state, default)).Allowed);
            }
            queue.Rows = [firstId, secondId];
            await using (var db = CreateFrameworkDbContext())
            {
                var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue,
                    topics: new EllaiTopicService(new PostgresEllaiTopicStore(db)));
                var original = await broker.InvokeAsync(context, originalRequest, default);
                var latest = await broker.InvokeAsync(context, latestRequest, default);
                Assert.Equal(firstId.ToString("D"), Assert.Single(original.Evidence).ResourceId);
                Assert.Equal(secondId.ToString("D"), Assert.Single(latest.Evidence).ResourceId);
                queue.Rows = [secondId];
                var revoked = await broker.InvokeAsync(context, originalRequest, default);
                Assert.True(revoked.Allowed);
                Assert.Empty(revoked.Evidence);
                Assert.DoesNotContain(firstId.ToString("D"), revoked.ResultSummary);
            }
        }
        finally
        {
            await using var cleanupDb = CreateFrameworkDbContext();
            await cleanupDb.Set<EllaiTopicEntity>().Where(topic => topic.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }

    private sealed class MutableHistoryQueue : SwiftAid.Application.Cases.ICaseQueueQuery
    {
        public IReadOnlyList<Guid> Rows { get; set; } = [];
        public Task<SwiftAid.Contracts.Cases.CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) =>
            Result(actor, null);
        public Task<SwiftAid.Contracts.Cases.CaseQueueResult> GetMineAsync(ActorContext actor, int? limit,
            SwiftAid.Application.Cases.CaseQueueFilter filter, CancellationToken ct) => Result(actor, filter.CaseIds);
        private Task<SwiftAid.Contracts.Cases.CaseQueueResult> Result(ActorContext actor, IReadOnlyList<Guid>? ids) =>
            Task.FromResult(new SwiftAid.Contracts.Cases.CaseQueueResult(SwiftAid.Contracts.Cases.CaseQueueAccess.Allowed,
                Rows.Where(id => ids is null || ids.Contains(id)).Select(id =>
                    new SwiftAid.Contracts.Cases.CaseQueueItem(id, "Reported", "SZ", actor.ActorId, 1, DateTimeOffset.UtcNow)).ToArray()));
    }

    [FrameworkPostgresFact]
    public async Task ConcurrentCaseHistoryWritesKeepEachResultInPostgres()
    {
        var sessionId = $"case-history-{Guid.NewGuid():N}";
        await using var initDb = CreateFrameworkDbContext();
        await initDb.Database.MigrateAsync();
        var context = new EllaiToolContext(sessionId, "scope-test", "cases", null, 4, true,
            Actor: Actor, DataClassification: EllaiProcessingClassification.SyntheticDemo);
        var ids = Enumerable.Range(1, 6).Select(_ => Guid.NewGuid()).ToArray();
        try
        {
            var writes = ids.Select(async id =>
            {
                await using var db = CreateFrameworkDbContext();
                var topics = new EllaiTopicService(new PostgresEllaiTopicStore(db));
                var memory = new EllaiCaseResultMemory(topics, new HistoryQueue());
                var row = new SwiftAid.Contracts.Cases.CaseQueueItem(id, "Reported", "SZ", Actor.ActorId, 1, DateTimeOffset.UtcNow);
                await memory.RememberAsync(context, [row], default);
            });
            await Task.WhenAll(writes);
            await using var verifyDb = CreateFrameworkDbContext();
            var verifyTopics = new EllaiTopicService(new PostgresEllaiTopicStore(verifyDb));
            var topic = await verifyTopics.GetByTitleAsync(sessionId, EllaiCaseResultMemory.TopicTitle, default);
            using var json = System.Text.Json.JsonDocument.Parse(topic!.EntityRef!);
            Assert.Equal(5, json.RootElement.GetProperty("Previous").GetArrayLength());
            Assert.False(json.RootElement.GetProperty("HistoryTruncated").GetBoolean());
            var original = await new EllaiCaseResultMemory(verifyTopics, new HistoryQueue()).RecallAsync(context, default, original: true);
            var latest = await new EllaiCaseResultMemory(verifyTopics, new HistoryQueue()).RecallAsync(context, default);
            Assert.True(original.Allowed);
            Assert.True(latest.Allowed);
            var originalId = Guid.Parse(Assert.Single(original.Evidence).ResourceId);
            var latestId = Guid.Parse(Assert.Single(latest.Evidence).ResourceId);
            Assert.Contains(originalId, ids);
            Assert.Contains(latestId, ids);
            Assert.NotEqual(originalId, latestId);
        }
        finally
        {
            await using var cleanupDb = CreateFrameworkDbContext();
            await cleanupDb.Set<EllaiTopicEntity>().Where(topic => topic.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }

    private sealed class HistoryQueue : SwiftAid.Application.Cases.ICaseQueueQuery
    {
        public Task<SwiftAid.Contracts.Cases.CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SwiftAid.Contracts.Cases.CaseQueueResult> GetMineAsync(ActorContext actor, int? limit,
            SwiftAid.Application.Cases.CaseQueueFilter filter, CancellationToken ct) =>
            Task.FromResult(new SwiftAid.Contracts.Cases.CaseQueueResult(SwiftAid.Contracts.Cases.CaseQueueAccess.Allowed,
                (filter.CaseIds ?? []).Select(id => new SwiftAid.Contracts.Cases.CaseQueueItem(id, "Reported", "SZ", actor.ActorId, 1, DateTimeOffset.UtcNow)).ToArray()));
    }

    [FrameworkPostgresFact]
    public async Task AtomicTopicPayloadUpdateFencesConcurrentWritersAcrossContexts()
    {
        var sessionId = $"topic-cas-{Guid.NewGuid():N}";
        await using var initialDb = CreateFrameworkDbContext();
        await initialDb.Database.MigrateAsync();
        var initial = await new PostgresEllaiTopicStore(initialDb).RecordAsync(sessionId, "History", [],
            "before", "v1", null, DateTimeOffset.UtcNow, default);
        try
        {
            await using var firstDb = CreateFrameworkDbContext();
            await using var secondDb = CreateFrameworkDbContext();
            var first = new PostgresEllaiTopicStore(firstDb);
            var second = new PostgresEllaiTopicStore(secondDb);
            var writes = await Task.WhenAll(
                first.CompareExchangePayloadAsync(sessionId, initial.TopicId, "before", "v1", "first", "v2", DateTimeOffset.UtcNow, default),
                second.CompareExchangePayloadAsync(sessionId, initial.TopicId, "before", "v1", "second", "v2", DateTimeOffset.UtcNow, default));
            Assert.Single(writes, result => result);
            await using var verifyDb = CreateFrameworkDbContext();
            var stored = await new PostgresEllaiTopicStore(verifyDb).GetAsync(sessionId, initial.TopicId, default);
            Assert.Contains(stored!.EntityRef, new[] { "first", "second" });
            Assert.Equal("v2", stored.EntityVersion);
            Assert.False(await first.CompareExchangePayloadAsync("another-session", initial.TopicId, stored.EntityRef, "v2", "foreign", "v3", DateTimeOffset.UtcNow, default));
        }
        finally
        {
            await using var cleanupDb = CreateFrameworkDbContext();
            await cleanupDb.Set<EllaiTopicEntity>().Where(topic => topic.SessionId == sessionId && topic.TopicId == initial.TopicId).ExecuteDeleteAsync();
        }
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
        await db.Database.MigrateAsync();
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
        await db.Database.MigrateAsync();
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
        await db.Database.MigrateAsync();
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
        await db.Database.MigrateAsync();
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
        await db.Database.MigrateAsync();
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
        await db.Database.MigrateAsync();
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
            await db.Database.MigrateAsync();
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
    public async Task PostgresLeaseRenewalProtectsCurrentAttemptAndRejectsReclaimedAttempt()
    {
        var id = $"framework-lease-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateFrameworkDbContext())
        {
            await db.Database.MigrateAsync();
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
    public async Task ProvisionalPublicationHoldsSessionAndAttemptFence()
    {
        var id = $"framework-publication-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateFrameworkDbContext())
        {
            await db.Database.MigrateAsync();
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
            Assert.False(await publisher.PublishIfCurrentAsync(id, "turn", 1, Actor.ActorId, "scope-test", 1,
                () => throw new InvalidOperationException("stale publication"), default));
            await cancelDb.Set<EllaiUtteranceEntity>().Where(turn => turn.SessionId == id && turn.ClientUtteranceId == "turn")
                .ExecuteUpdateAsync(update => update.SetProperty(turn => turn.AttemptCount, 2));
            Assert.False(await publisher.PublishIfCurrentAsync(id, "turn", 1, Actor.ActorId, "scope-test", 2,
                () => throw new InvalidOperationException("reclaimed publication"), default));
            Assert.True(await publisher.PublishIfCurrentAsync(id, "turn", 2, Actor.ActorId, "scope-test", 2,
                () => true, default));
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
            await db.Database.MigrateAsync();
            var store = new PostgresEllaiSessionStore(db);
            await store.CreateAsync(session, default);
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
                first.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(first, first.ClientUtteranceId)));
        }
        using var model = new LongRecoveryClient();
        await RunFrameworkWorkerAsync(id, "turn-1", model, null);
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
            await db.Database.MigrateAsync();
            var store = new PostgresEllaiSessionStore(db);
            await store.CreateAsync(session, default);
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
                first.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(first, first.ClientUtteranceId)));
        }

        using var model = new RecoveryClient();
        await RunFrameworkWorkerAsync(id, "turn-1", model, cache);
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
        Assert.Empty(await verified.ReadConversationAsync(id, "different-owner", default));
        Assert.Contains("First synthetic question", model.LastMessages);
        Assert.Contains("Synthetic recovered answer.", model.LastMessages);
        Assert.Contains("Second synthetic question", model.LastMessages);
        Assert.Equal(2, (await new PostgresEllaiUsageStore(verifyDb).TotalsAsync(id, default)).Calls);
        Assert.Equal(0.000026m, await new PostgresEllaiUsageStore(verifyDb).SpendForAsync(id, default));
    }

    private static async Task RunFrameworkWorkerAsync(string sessionId, string turnId, IChatClient model, ICacheStore? cache,
        TimeSpan? executionTimeout = null, EllaiUiRegistry? registry = null)
    {
        var consent = new EllaiConsentService();
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateFrameworkDbContext());
        services.AddScoped<PostgresEllaiSessionStore>();
        services.AddScoped<IEllaiSessionStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUtteranceOutboxStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
        services.AddSingleton(consent);
        if (cache is not null) services.AddSingleton(cache);
        services.AddEllaiUsageAccounting(new EllaiOptions { SpendingCapDollars = 70m,
            PricingInputPer1KTokens = 0.001m, PricingOutputPer1KTokens = 0.002m });
        services.AddSingleton(new EllaiSessionResponseStore());
        services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent, uiRegistry: registry));
        services.AddScoped(_ => new EllaiPromptBuilder("Synthetic test policy", "fixture"));
        services.AddEllaiReasoningExecutor(new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture"
        }, "framework");
        services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, model);
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
