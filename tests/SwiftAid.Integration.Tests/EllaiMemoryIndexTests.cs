using SwiftAid.Infrastructure.Memory;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Intelligence.Meta;
using Microsoft.Extensions.AI;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Ellai;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Contracts.Ellai;
using System.Runtime.CompilerServices;

namespace SwiftAid.Integration.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T09")]
[Collection("Ellai framework PostgreSQL worker")]
public sealed class EllaiMemoryIndexTests
{
    private sealed class FrameworkPostgresFactAttribute : FactAttribute
    {
        public FrameworkPostgresFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES") != "1")
                Skip = "Requires the dedicated disposable framework PostgreSQL fixture.";
        }
    }

    [FrameworkPostgresFact]
    public async Task FrameworkWorkerRecoversProviderOutageAfterWorkerRestartWithPostgres()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        var sessionId = "framework-retry-" + Guid.NewGuid().ToString("N");
        var actor = fixture.CreateActor();
        await using (var db = fixture.CreateDbContext())
        {
            var store = new PostgresEllaiSessionStore(db);
            var session = new EllaiSessionRecord(sessionId, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
                actor.ActorId, "scope-1", null, null, DateTimeOffset.UtcNow);
            await store.CreateAsync(session, default);
            var work = new EllaiUtteranceWork(sessionId, "scope-1", "Synthetic question", "public.product-help", null,
                EllaiMode.Ask, 1, "cycle-1", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
                EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "turn-1");
            Assert.NotNull(await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
                work.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId)));
        }

        using var model = new RecoveringProviderClient();
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext());
        services.AddScoped<PostgresEllaiSessionStore>();
        services.AddScoped<IEllaiSessionStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUtteranceOutboxStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
        services.AddSingleton<EllaiConsentService>().AddSingleton<EllaiUsageLedger>().AddSingleton<EllaiSessionResponseStore>();
        services.AddScoped(sp => new EllaiToolBroker(new EllaiPolicyService(), sp.GetRequiredService<EllaiConsentService>()));
        services.AddScoped<IEllaiReasoningExecutor>(sp => new EllaiFrameworkExecutor(model,
            new EllaiPromptBuilder("Synthetic policy", "fixture"), sp.GetRequiredService<EllaiToolBroker>(),
            sp.GetRequiredService<EllaiUsageLedger>(), sp.GetRequiredService<IEllaiUsageStore>()));
        await using var provider = services.BuildServiceProvider();
        await using var restartedProvider = services.BuildServiceProvider();
        Assert.NotSame(provider.GetRequiredService<EllaiUsageLedger>(), restartedProvider.GetRequiredService<EllaiUsageLedger>());
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<EllaiConversationTurn> ReadTurnAsync()
        {
            await using var db = fixture.CreateDbContext();
            return Assert.Single(await new PostgresEllaiSessionStore(db).ReadConversationAsync(sessionId, actor.ActorId, timeout.Token));
        }
        try
        {
            using (var first = new EllaiUtteranceWorker(provider, loggerFactory.CreateLogger<EllaiUtteranceWorker>()))
            {
                await first.StartAsync(default);
                try
                {
                    EllaiConversationTurn failed;
                    do { await Task.Delay(25, timeout.Token); failed = await ReadTurnAsync(); }
                    while (failed.Outcome != "RetryableFailure");
                    Assert.Null(failed.AssistantText);
                }
                finally { await first.StopAsync(CancellationToken.None); }
            }
            using (var restarted = new EllaiUtteranceWorker(restartedProvider, loggerFactory.CreateLogger<EllaiUtteranceWorker>()))
            {
                await restarted.StartAsync(default);
                try
                {
                    EllaiConversationTurn recovered;
                    do { await Task.Delay(25, timeout.Token); recovered = await ReadTurnAsync(); }
                    while (recovered.CompletedAtUtc is null);
                    Assert.Equal("Completed", recovered.Outcome);
                    Assert.Equal("Recovered.", recovered.AssistantText);
                    Assert.Equal(2, recovered.AttemptCount);
                    Assert.Equal(2, model.Calls);
                    await using var db = fixture.CreateDbContext();
                    var persisted = await new PostgresEllaiUsageStore(db).TotalsAsync(sessionId, timeout.Token);
                    Assert.Equal(2, persisted.Calls);
                    Assert.Equal(persisted.TotalTokens, restartedProvider.GetRequiredService<EllaiUsageLedger>().TotalsFor(sessionId).TotalTokens);
                }
                finally { await restarted.StopAsync(CancellationToken.None); }
            }
        }
        finally
        {
            await using var db = fixture.CreateDbContext();
            await db.Set<EllaiUsageSampleEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            await db.Set<EllaiUtteranceEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            await db.Set<EllaiCheckpointEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            await db.Set<EllaiSessionEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }

    private sealed class RecoveringProviderClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (++Calls == 1) throw new HttpRequestException("Synthetic provider outage");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Recovered.","spokenText":"Recovered.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(string Method, string Url, string Body, bool HasKey)> Seen = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            Seen.Add((request.Method.Method, request.RequestUri?.ToString() ?? string.Empty, body,
                request.Headers.Contains("api-key")));
            return Task.FromResult(respond(request));
        }
    }

    private static EllaiMemoryIndex Index(CaptureHandler handler) =>
        new(new HttpClient(handler), "http://qdrant:6333", "test-key", "ellai-memory");

    [Fact]
    public async Task UpsertPinsSessionAndScopeInPayload()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"ok"}""")
        });
        var result = await Index(handler).UpsertAsync(
            new EllaiMemoryPoint("m-1", "sess-1", "epoch-1", "topic-1", "summary",
                new[] { "e1" }, DateTimeOffset.UtcNow),
            new[] { 0.1f, 0.2f }, CancellationToken.None);

        Assert.True(result);
        var call = Assert.Single(handler.Seen);
        Assert.Contains("/collections/ellai-memory/points", call.Url, StringComparison.Ordinal);
        Assert.Contains("sess-1", call.Body, StringComparison.Ordinal);
        Assert.Contains("epoch-1", call.Body, StringComparison.Ordinal);
        Assert.True(call.HasKey);
    }

    [Fact]
    public async Task SearchAlwaysFiltersBothSessionAndScope()
    {
        const string response = """{"result":[{"id":"m-1","payload":{"session_id":"sess-1","scope_epoch":"epoch-1","topic_id":"t","summary":"s","evidence_ids":["e1"]}}]}""";
        var handler = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        });
        var points = await Index(handler).SearchAsync(
            new[] { 0.1f }, "sess-1", "epoch-1", 5, CancellationToken.None);

        var call = Assert.Single(handler.Seen);
        Assert.Contains("/points/search", call.Url, StringComparison.Ordinal);
        Assert.Contains("session_id", call.Body, StringComparison.Ordinal);
        Assert.Contains("scope_epoch", call.Body, StringComparison.Ordinal);
        var point = Assert.Single(points);
        Assert.Equal("sess-1", point.SessionId);
        Assert.Equal("epoch-1", point.ScopeEpoch);
    }

    [Fact]
    public async Task TransportFailureFailsClosedWithEmptyRecall()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        var points = await Index(handler).SearchAsync(
            new[] { 0.1f }, "sess-1", "epoch-1", 5, CancellationToken.None);

        Assert.Empty(points);
    }

    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("ELLAI_LIVE_QDRANT"), "1", StringComparison.Ordinal))
            {
                Skip = "Set ELLAI_LIVE_QDRANT=1 with QDRANT_TEST_ENDPOINT/QDRANT_TEST_API_KEY to run the live Qdrant round-trip.";
            }
        }
    }

    private sealed class LiveFrameworkStoresFactAttribute : FactAttribute
    {
        public LiveFrameworkStoresFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE_QDRANT") != "1" ||
                Environment.GetEnvironmentVariable("SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES") != "1")
                Skip = "Enable both dedicated disposable framework PostgreSQL and Qdrant fixtures.";
        }
    }

    private sealed class LiveMuseFrameworkStoresFactAttribute : FactAttribute
    {
        public LiveMuseFrameworkStoresFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
                Environment.GetEnvironmentVariable("ELLAI_LIVE_QDRANT") != "1" ||
                Environment.GetEnvironmentVariable("SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES") != "1")
                Skip = "Requires live Muse and dedicated disposable PostgreSQL and Qdrant fixtures.";
        }
    }

    [LiveFrameworkStoresFact]
    public Task FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant() => RunFrameworkRecallAsync(liveModel: false);

    [LiveMuseFrameworkStoresFact]
    public Task LiveMuseWorkerRecallsAuthorizedCaseFromPostgresAndQdrant() => RunFrameworkRecallAsync(liveModel: true);

    private static async Task RunFrameworkRecallAsync(bool liveModel)
    {
        var endpoint = Environment.GetEnvironmentVariable("QDRANT_TEST_ENDPOINT")!;
        Assert.True(new Uri(endpoint).IsLoopback);
        var key = Environment.GetEnvironmentVariable("QDRANT_TEST_API_KEY") ?? "";
        var collection = "ellai-worker-authority-" + Guid.NewGuid().ToString("N");
        var sessionId = "framework-worker-authority-" + Guid.NewGuid().ToString("N");
        using var transport = new SwitchableQdrantHandler();
        using var http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(5) };
        var index = new EllaiMemoryIndex(http, endpoint, key, collection);
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        try
        {
            Assert.True(await index.EnsureExactCollectionAsync(CancellationToken.None));
            var actor = fixture.CreateActor();
            var context = new EllaiToolContext(sessionId, "scope-1", "cases", null, 4, true,
                "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive, Actor: actor,
                AllowedTools: new HashSet<string> { EllaiToolNames.Topic });
            await using (var db = fixture.CreateDbContext())
            {
                var session = new EllaiSessionRecord(sessionId, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
                    actor.ActorId, context.ScopeEpoch, null, null, DateTimeOffset.UtcNow);
                var sessionStore = new PostgresEllaiSessionStore(db);
                await sessionStore.CreateAsync(session, default);
                var work = new EllaiUtteranceWork(sessionId, context.ScopeEpoch, liveModel
                    ? "Use ellai_topic with ref case-results to recall my cases. State the case identifier returned by the tool."
                    : "Recall my cases", "cases", null,
                    EllaiMode.Ask, 1, "cycle-1", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
                    EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.Topic },
                    EllaiConsentPurposes.PublicProductHelp, "turn-1");
                Assert.NotNull(await sessionStore.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
                    work.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId)));
                var topics = new EllaiTopicService(new PostgresEllaiTopicStore(db));
                var unit = new PostgresCaseCommandUnitOfWork(db);
                var query = new CaseQueueQuery(new PostgresCaseQueueStore(db),
                    new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)), unit);
                await new EllaiCaseResultMemory(topics, query).RememberAsync(context,
                    [new CaseQueueItem(fixture.CaseId, "Reported", "SZ", fixture.ActorId, 2, DateTimeOffset.UtcNow)], default);
                var topic = Assert.Single(await new PostgresEllaiTopicStore(db).ListAsync(sessionId, default));
                Assert.True(await index.UpsertExactAsync(new("case-results", sessionId, context.ScopeEpoch, topic.TopicId,
                    EllaiCaseResultMemory.SourceVersion(topic.EntityRef!), DateTimeOffset.UtcNow.AddMinutes(5)), default));
                Assert.NotNull(await index.FindExactAsync("case-results", sessionId, context.ScopeEpoch, default));
            }
            var consent = new EllaiConsentService();
            using var model = new WorkerRecallClient(fixture.CaseId);
            var services = new ServiceCollection();
            services.AddScoped(_ => fixture.CreateDbContext());
            services.AddScoped<PostgresEllaiSessionStore>();
            services.AddScoped<IEllaiSessionStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
            services.AddScoped<IEllaiUtteranceOutboxStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
            services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
            services.AddSingleton<IEllaiExactReferenceIndex>(index);
            services.AddSingleton(consent).AddSingleton<EllaiUsageLedger>().AddSingleton<EllaiSessionResponseStore>();
            services.AddScoped<ICaseQueueQuery>(sp =>
            {
                var db = sp.GetRequiredService<SwiftAid.Infrastructure.Persistence.SwiftAidDbContext>();
                var unit = new PostgresCaseCommandUnitOfWork(db);
                return new CaseQueueQuery(new PostgresCaseQueueStore(db),
                    new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)), unit);
            });
            services.AddScoped(sp =>
            {
                var db = sp.GetRequiredService<SwiftAid.Infrastructure.Persistence.SwiftAidDbContext>();
                return new EllaiToolBroker(new EllaiPolicyService(), consent, queue: sp.GetRequiredService<ICaseQueueQuery>(),
                    topics: new EllaiTopicService(new PostgresEllaiTopicStore(db)), referenceIndex: sp.GetRequiredService<IEllaiExactReferenceIndex>());
            });
            services.AddScoped(_ => new EllaiPromptBuilder("Synthetic policy", "fixture"));
            var modelOptions = liveModel ? EllaiOptions.Bind(Environment.GetEnvironmentVariable) : new EllaiOptions
            {
                Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture"
            };
            services.AddEllaiReasoningExecutor(modelOptions, "framework");
            using var diagnostics = liveModel ? new LiveDecisionDiagnosticsClient(EllaiMuseChatClientFactory.Create(modelOptions)) : null;
            services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, diagnostics ?? (IChatClient)model);
            await using var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
                Assert.IsType<EllaiFrameworkExecutor>(scope.ServiceProvider.GetRequiredService<IEllaiReasoningExecutor>());
            using var logs = LoggerFactory.Create(builder => builder.AddConsole());
            using var worker = new EllaiUtteranceWorker(provider, logs.CreateLogger<EllaiUtteranceWorker>());
            var recallRequests = transport.RecallRequests;
            await worker.StartAsync(default);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(liveModel ? 180 : 30));
                EllaiConversationTurn turn;
                do
                {
                    await Task.Delay(50, timeout.Token);
                    await using var db = fixture.CreateDbContext();
                    turn = Assert.Single(await new PostgresEllaiSessionStore(db).ReadConversationAsync(sessionId, actor.ActorId, timeout.Token));
                } while (turn.CompletedAtUtc is null);
                var provisional = provider.GetRequiredService<EllaiSessionResponseStore>().ReadSince(sessionId, 0).Responses
                    .Count(item => !item.IsFinal && item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
                Assert.True(turn.Outcome == "Completed", $"Initial outcome: {turn.Outcome}; provisional={provisional}; {diagnostics?.Summary ?? "scripted model"}");
                Assert.True(transport.RecallRequests > recallRequests);
                var trace = System.Text.Json.JsonSerializer.Deserialize<EllaiToolTrace>(turn.ToolTraceJson!);
                Assert.NotNull(trace);
                Assert.False(trace.HistoryIncluded);
                Assert.Contains(trace.Observations, item => item.ToolName == EllaiToolNames.Topic &&
                    item.Evidence.Any(reference => reference.ResourceId == fixture.CaseId.ToString("D")));
                if (liveModel) Assert.Contains(fixture.CaseId.ToString("D"), turn.AssistantText!, StringComparison.OrdinalIgnoreCase);
                else Assert.Contains(fixture.CaseId.ToString("D"), model.Observation);
                transport.Unavailable = true;
                async Task RunFollowupAsync(string turnId, bool removeTopicTool = false, bool expectDenied = false)
                {
                    var priorRecallRequests = transport.RecallRequests;
                    model.Reset();
                    model.SkipTool = removeTopicTool;
                    await using (var db = fixture.CreateDbContext())
                    {
                        var store = new PostgresEllaiSessionStore(db);
                        var current = (await store.GetAsync(sessionId, timeout.Token))!;
                        var followup = new EllaiUtteranceWork(sessionId, context.ScopeEpoch, liveModel
                            ? "Use ellai_topic with ref case-results again. State the case identifiers currently returned by the tool."
                            : "Recall again", "cases", null,
                            EllaiMode.Ask, 1, "cycle-" + turnId, actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
                            EllaiProcessingClassification.UserProvidedNonSensitive, removeTopicTool ? new HashSet<string>() : new HashSet<string> { EllaiToolNames.Topic },
                            EllaiConsentPurposes.PublicProductHelp, turnId);
                        Assert.NotNull(await store.TryAcceptUtteranceAsync(current with
                            { SessionVersion = current.SessionVersion + 1, CyclesUsed = current.CyclesUsed + 1 }, current.SessionVersion,
                            followup.ClientUtteranceId, timeout.Token, EllaiUtteranceWorkEnvelope.Serialize(followup, followup.ClientUtteranceId)));
                    }
                    do
                    {
                        await Task.Delay(50, timeout.Token);
                        await using var db = fixture.CreateDbContext();
                        turn = (await new PostgresEllaiSessionStore(db).ReadConversationAsync(sessionId, actor.ActorId, timeout.Token))
                            .Single(item => item.ClientUtteranceId == turnId);
                    } while (turn.CompletedAtUtc is null);
                    if (expectDenied)
                    {
                        Assert.NotEqual("Completed", turn.Outcome);
                        Assert.Null(turn.AssistantText);
                        return;
                    }
                    var provisionalCount = provider.GetRequiredService<EllaiSessionResponseStore>().ReadSince(sessionId, 0).Responses
                        .Count(item => item.ClientUtteranceId == turnId && !item.IsFinal && item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
                    Assert.True(turn.Outcome == "Completed", $"Follow-up outcome: {turn.Outcome}; provisional={provisionalCount}; {diagnostics?.Summary ?? "scripted model"}");
                    if (removeTopicTool) Assert.Equal(priorRecallRequests, transport.RecallRequests);
                    else Assert.True(transport.RecallRequests > priorRecallRequests);
                    var followupTrace = System.Text.Json.JsonSerializer.Deserialize<EllaiToolTrace>(turn.ToolTraceJson!);
                    Assert.True(followupTrace!.HistoryIncluded);
                    if (!liveModel && !removeTopicTool) Assert.Contains("\"allowed\":true", model.Observation);
                }
                await RunFollowupAsync("turn-2");
                Assert.True(transport.FailedRequests > 0);
                if (liveModel) Assert.Contains(fixture.CaseId.ToString("D"), turn.AssistantText!, StringComparison.OrdinalIgnoreCase);
                else
                {
                    Assert.Contains(fixture.CaseId.ToString("D"), model.Observation);
                    Assert.Contains("Recall my cases", model.FirstMessages);
                    Assert.Contains(model.FirstMessages, text => text.Contains("Done.", StringComparison.Ordinal));
                    model.BeforeFinal = () => fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
                    await RunFollowupAsync("turn-revoked-before-final", expectDenied: true);
                    Assert.Contains(fixture.CaseId.ToString("D"), model.Observation);
                    model.BeforeFinal = null;
                    await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddHours(1));
                    async Task<EllaiUtteranceOutcome> RunWithoutProvisionalAsync()
                    {
                        model.Reset();
                        await using var scope = provider.CreateAsyncScope();
                        var broker = scope.ServiceProvider.GetRequiredService<EllaiToolBroker>();
                        var work = new EllaiUtteranceWork(sessionId, context.ScopeEpoch, "Recall again", "cases", null,
                            EllaiMode.Ask, 1, "no-provisional", actor, true, "consent/0",
                            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
                            new HashSet<string> { EllaiToolNames.Topic }, EllaiConsentPurposes.PublicProductHelp, "no-provisional");
                        return await EllaiUtteranceRunner.RunOneAsync(work,
                            new(scope.ServiceProvider.GetRequiredService<IEllaiReasoningExecutor>(), null, null, consent,
                                InvokeToolAsync: broker.InvokeAsync, CaseQueue: scope.ServiceProvider.GetRequiredService<ICaseQueueQuery>()),
                            1024, timeout.Token);
                    }
                    var allowedWithoutProvisional = await RunWithoutProvisionalAsync();
                    Assert.True(allowedWithoutProvisional.Completed, allowedWithoutProvisional.StopReason);
                    Assert.Contains(fixture.CaseId.ToString("D"), model.Observation);
                    model.BeforeFinal = () => fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
                    var revokedWithoutProvisional = await RunWithoutProvisionalAsync();
                    Assert.False(revokedWithoutProvisional.Completed);
                    Assert.Null(revokedWithoutProvisional.DisplayText);
                    Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, revokedWithoutProvisional.FailureCode);
                    Assert.Contains(fixture.CaseId.ToString("D"), model.Observation);
                    model.BeforeFinal = null;
                    await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddHours(1));
                    model.StreamCaseSegment = true;
                    var streamingTurn = RunFollowupAsync("turn-stream-revoked", expectDenied: true);
                    await model.SegmentSent.Task.WaitAsync(timeout.Token);
                    var published = provider.GetRequiredService<EllaiSessionResponseStore>().ReadSince(sessionId, 0, take: 64).Responses;
                    Assert.Contains(published, response => response.ClientUtteranceId == "turn-stream-revoked" &&
                        !response.IsFinal && response.Text.Contains(fixture.CaseId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                    await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
                    model.ReleaseSegment.TrySetResult();
                    await streamingTurn;
                    model.StreamCaseSegment = false;
                    await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddHours(1));
                    await RunFollowupAsync("turn-tool-removed", removeTopicTool: true);
                    Assert.DoesNotContain(model.FirstMessages, text => text.Contains(fixture.CaseId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                    Assert.Contains(model.FirstMessages, text => text.Contains("could not be verified", StringComparison.Ordinal));

                    // Start with an available index so history reauthorization succeeds,
                    // then lose it only after the framework asks to invoke the tool.
                    transport.Unavailable = false;
                    var priorFailures = transport.FailedRequests;
                    model.BeforeToolCall = () => transport.Unavailable = true;
                    await RunFollowupAsync("turn-outage-during-execution");
                    model.BeforeToolCall = null;
                    Assert.True(transport.FailedRequests > priorFailures);
                    Assert.Contains(fixture.CaseId.ToString("D"), model.Observation);

                    // The derived request ignores cancellation; PostgreSQL recall
                    // must finish before that request is released.
                    transport.Hold = true;
                    await RunFollowupAsync("turn-held-index");
                    Assert.True(transport.HeldStarted.Task.IsCompleted);
                    Assert.False(transport.HeldRelease.Task.IsCompleted);
                    Assert.Contains(fixture.CaseId.ToString("D"), model.Observation);
                    transport.Hold = false;
                    transport.HeldRelease.TrySetResult();
                    using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (Volatile.Read(ref transport.HeldActive) > 0)
                        await Task.Delay(10, drain.Token);
                }
                await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
                await RunFollowupAsync("turn-3");
                if (liveModel) Assert.DoesNotContain(fixture.CaseId.ToString("D"), turn.AssistantText!, StringComparison.OrdinalIgnoreCase);
                else
                {
                    Assert.DoesNotContain(fixture.CaseId.ToString("D"), model.Observation);
                    Assert.DoesNotContain(model.FirstMessages, text => text.Contains(fixture.CaseId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                    Assert.Contains(model.FirstMessages, text => text.Contains("could not be verified", StringComparison.Ordinal));
                }
            }
            finally { transport.HeldRelease.TrySetResult(); await worker.StopAsync(CancellationToken.None); }
        }
        finally
        {
            transport.HeldRelease.TrySetResult();
            transport.Unavailable = false;
            await using var db = fixture.CreateDbContext();
            await db.Set<EllaiUsageSampleEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            await db.Set<EllaiUtteranceEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            await db.Set<EllaiCheckpointEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            await db.Set<EllaiSessionEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            await db.Set<EllaiTopicEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            using var delete = new HttpRequestMessage(HttpMethod.Delete, endpoint.TrimEnd('/') + "/collections/" + collection + "-exact");
            if (!string.IsNullOrWhiteSpace(key)) delete.Headers.TryAddWithoutValidation("api-key", key);
            using var result = await http.SendAsync(delete);
            Assert.True(result.IsSuccessStatusCode);
        }
    }

    // Capture only bounded shape diagnostics, never model text, tool arguments or case identifiers.
    private sealed class LiveDecisionDiagnosticsClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        private readonly List<string> _calls = [];
        public string Summary => string.Join("; ", _calls);

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = new System.Text.StringBuilder();
            var toolCalls = 0;
            var overflow = false;
            try
            {
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    toolCalls += update.Contents.OfType<FunctionCallContent>().Count();
                    if (update.Text.Length <= 512_000 - text.Length) text.Append(update.Text);
                    else overflow = true;
                    yield return update;
                }
            }
            finally
            {
                var value = text.ToString();
                var parsed = EllaiModelTurnParser.Parse(value);
                var decision = parsed.Turn?.FinalDecision;
                var validationCount = decision is null ? -1 : EllaiDecisionValidator.Validate(decision).Count;
                var segmentCount = -1;
                var displayMatches = false;
                var speechMatches = false;
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(value);
                    if (json.RootElement.TryGetProperty("stream_segments", out var segments) && segments.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var parts = segments.EnumerateArray().Select(segment =>
                            segment.TryGetProperty("text", out var item) && item.ValueKind == System.Text.Json.JsonValueKind.String
                                ? item.GetString() ?? string.Empty : string.Empty).ToArray();
                        segmentCount = parts.Length;
                        var joined = string.Join(" ", parts);
                        displayMatches = decision is not null && joined == decision.DisplayText.Trim();
                        speechMatches = decision is not null && joined == decision.SpokenText?.Trim();
                    }
                }
                catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException) { }
                _calls.Add($"chars={text.Length},overflow={overflow},toolCalls={toolCalls},parse={(decision is null ? EllaiModelTurnParser.DiagnosticCode(parsed.Error) : "decision")},validation={validationCount},segments={segmentCount},displayMatches={displayMatches},speechMatches={speechMatches}");
            }
        }
    }

    private sealed class WorkerRecallClient(Guid syntheticCaseId) : IChatClient
    {
        private int _calls;
        public string Observation { get; private set; } = "";
        public IReadOnlyList<string> FirstMessages { get; private set; } = [];
        public bool SkipTool { get; set; }
        public Action? BeforeToolCall { get; set; }
        public Func<Task>? BeforeFinal { get; set; }
        public bool StreamCaseSegment { get; set; }
        public TaskCompletionSource SegmentSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSegment { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { _calls = 0; Observation = ""; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var contents = new List<AIContent>();
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
                contents.AddRange(update.Contents);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (++_calls == 1)
            {
                FirstMessages = messages.Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
                    .Select(message => message.Text).ToArray();
                if (SkipTool)
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
                else
                {
                    BeforeToolCall?.Invoke();
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call", EllaiToolNames.Topic,
                        new Dictionary<string, object?> { ["ref"] = "case-results" })]);
                }
            }
            else
            {
                Observation = string.Join(" ", messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(content => content.Result?.ToString()));
                if (BeforeFinal is not null) await BeforeFinal();
                if (StreamCaseSegment)
                {
                    using var payload = System.Text.Json.JsonDocument.Parse(Observation);
                    var summary = payload.RootElement.GetProperty("resultSummary").GetString()!;
                    var segment = summary.Split('\n').Single(line => line.Contains(syntheticCaseId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                    var quoted = System.Text.Json.JsonSerializer.Serialize(segment);
                    var reference = payload.RootElement.GetProperty("evidence")[0].GetRawText();
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        "{\"stream_segments\":[{\"text\":" + quoted + ",\"claims\":[{\"claimId\":\"case\",\"text\":" + quoted +
                        ",\"kind\":\"Observed\",\"evidence\":[" + reference + "]}]}]");
                    SegmentSent.TrySetResult();
                    await ReleaseSegment.Task.WaitAsync(cancellationToken);
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        ",\"final_decision\":{\"schemaVersion\":2,\"kind\":\"Answer\",\"displayText\":" + quoted +
                        ",\"spokenText\":" + quoted + ",\"claims\":[],\"questions\":[],\"uiIntents\":[],\"planProposal\":null,\"topicRefs\":[],\"resumeSuggestion\":null,\"stopReason\":null}}");
                    yield break;
                }
                var answer = Observation.Contains(syntheticCaseId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                    ? "Done. " + syntheticCaseId.ToString("D") : "Done.";
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}"""
                        .Replace("Done.", answer, StringComparison.Ordinal));
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 4 })]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SwitchableQdrantHandler() : DelegatingHandler(new HttpClientHandler())
    {
        public volatile bool Unavailable;
        public volatile bool Hold;
        public TaskCompletionSource HeldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeldRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HeldActive;
        public int FailedRequests;
        public int RecallRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/points/scroll", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref RecallRequests);
                if (Hold) return HoldAsync();
            }
            if (!Unavailable) return base.SendAsync(request, cancellationToken);
            Interlocked.Increment(ref FailedRequests);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Synthetic Qdrant outage."));
        }

        private async Task<HttpResponseMessage> HoldAsync()
        {
            Interlocked.Increment(ref HeldActive);
            HeldStarted.TrySetResult();
            try
            {
                await HeldRelease.Task;
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            }
            finally { Interlocked.Decrement(ref HeldActive); }
        }
    }

    [LiveFrameworkStoresFact]
    public async Task FrameworkRecallUsesPostgresAuthorityWithLiveQdrant()
    {
        var endpoint = Environment.GetEnvironmentVariable("QDRANT_TEST_ENDPOINT")!;
        Assert.True(new Uri(endpoint).IsLoopback);
        var key = Environment.GetEnvironmentVariable("QDRANT_TEST_API_KEY") ?? "";
        var collection = "ellai-framework-authority-" + Guid.NewGuid().ToString("N");
        var sessionId = "framework-authority-" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var index = new EllaiMemoryIndex(http, endpoint, key, collection);
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        try
        {
            Assert.True(await index.EnsureExactCollectionAsync(CancellationToken.None));
            await using var db = fixture.CreateDbContext();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var query = new CaseQueueQuery(new PostgresCaseQueueStore(db),
                new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)), unit);
            var topics = new EllaiTopicService(new PostgresEllaiTopicStore(db));
            var context = new EllaiToolContext(sessionId, "scope-1", "cases", null, 4, true,
                "consent/1", EllaiProcessingClassification.SyntheticDemo, Actor: fixture.CreateActor(),
                AllowedTools: new HashSet<string> { EllaiToolNames.Topic });
            var row = new CaseQueueItem(fixture.CaseId, "Reported", "SZ", fixture.ActorId, 2, DateTimeOffset.UtcNow);
            await new EllaiCaseResultMemory(topics, query).RememberAsync(context, [row], CancellationToken.None);
            var topic = Assert.Single(await new PostgresEllaiTopicStore(db).ListAsync(sessionId, CancellationToken.None));
            Assert.True(await index.UpsertExactAsync(new("case-results", sessionId, context.ScopeEpoch, topic.TopicId,
                EllaiCaseResultMemory.SourceVersion(topic.EntityRef!), DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None));
            Assert.NotNull(await index.FindExactAsync("case-results", sessionId, context.ScopeEpoch, CancellationToken.None));
            var consent = new EllaiConsentService();
            consent.Grant(sessionId, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
                EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
            var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: query, topics: topics, referenceIndex: index);
            async Task<string> RecallAsync()
            {
                using var client = new RecallClient();
                var agent = new EllaiFrameworkAgent(client);
                EllaiToolRequest? invoked = null;
                EllaiToolResult? observed = null;
                var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "recall")], 300,
                    _ => Task.FromResult(true), async (toolContext, request, ct) =>
                    {
                        invoked = request;
                        return observed = await broker.InvokeAsync(toolContext, request, ct);
                    },
                    new EllaiFrameworkUsage(sessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
                    revalidateStreamObservations: async ct =>
                    {
                        if (invoked is null || observed is null) return false;
                        var current = await broker.InvokeAsync(context, invoked, ct);
                        return current.Allowed && current.ResultSummary == observed.ResultSummary &&
                            current.Evidence.SequenceEqual(observed.Evidence);
                    });
                Assert.NotNull(decision);
                return client.Observation;
            }
            Assert.Contains(fixture.CaseId.ToString("D"), await RecallAsync());
            await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.DoesNotContain(fixture.CaseId.ToString("D"), await RecallAsync());
        }
        finally
        {
            await using var cleanup = fixture.CreateDbContext();
            await cleanup.Set<EllaiTopicEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            using var delete = new HttpRequestMessage(HttpMethod.Delete, endpoint.TrimEnd('/') + "/collections/" + collection + "-exact");
            if (!string.IsNullOrWhiteSpace(key)) delete.Headers.TryAddWithoutValidation("api-key", key);
            using var result = await http.SendAsync(delete);
            Assert.True(result.IsSuccessStatusCode);
        }
    }

    [LiveFact]
    public async Task LiveRoundTripStoresAndRecallsScopedPoint()
    {
        var endpoint = Environment.GetEnvironmentVariable("QDRANT_TEST_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("QDRANT_TEST_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var index = new EllaiMemoryIndex(http, endpoint!, key ?? string.Empty, "ellai-memory-live-test");

        Assert.True(await index.EnsureCollectionAsync(4, CancellationToken.None));
        var vector = new[] { 1f, 0f, 0f, 0f };
        Assert.True(await index.UpsertAsync(
            new EllaiMemoryPoint("live-1", "sess-live", "epoch-live", "topic-live",
                "live round-trip", new[] { "e1" }, DateTimeOffset.UtcNow),
            vector, CancellationToken.None));

        var points = await index.SearchAsync(vector, "sess-live", "epoch-live", 5, CancellationToken.None);
        Assert.Contains(points, p => p.Id == "live-1" && p.SessionId == "sess-live");

        var otherScope = await index.SearchAsync(vector, "sess-live", "epoch-other", 5, CancellationToken.None);
        Assert.DoesNotContain(otherScope, p => p.Id == "live-1");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, endpoint!.TrimEnd('/') + "/collections/ellai-memory-live-test");
        if (!string.IsNullOrWhiteSpace(key)) delete.Headers.TryAddWithoutValidation("api-key", key);
        using var response = await http.SendAsync(delete, CancellationToken.None);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ExactWritesContainNoEmbeddingOrNarrative()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var index = Index(handler);
        Assert.True(await index.EnsureExactCollectionAsync(CancellationToken.None));
        Assert.True(await index.UpsertExactAsync(new("m", "s", "e", "t", "v1", DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None));
        using var collection = System.Text.Json.JsonDocument.Parse(handler.Seen[0].Body);
        Assert.Empty(collection.RootElement.GetProperty("vectors").EnumerateObject());
        using var point = System.Text.Json.JsonDocument.Parse(handler.Seen[1].Body);
        Assert.Empty(point.RootElement.GetProperty("points")[0].GetProperty("vector").EnumerateObject());
        Assert.DoesNotContain("summary", handler.Seen[1].Body);
        Assert.All(handler.Seen, request => Assert.True(request.HasKey));
    }

    [Fact]
    public async Task ExactLookupFailsClosedWhenIndexIsUnavailable()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        Assert.Null(await Index(handler).FindExactAsync("case-results", "session", "epoch", CancellationToken.None));
        Assert.True(Assert.Single(handler.Seen).HasKey);
    }

    [Fact]
    public async Task ExactLookupRejectsMismatchedRemotePayload()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"result":{"points":[{"payload":{"memory_id":"m","session_id":"another","scope_epoch":"e","encoding":"exact/1"}}]}}""")
        });
        Assert.Null(await Index(handler).FindExactAsync("m", "s", "e", CancellationToken.None));
        Assert.Contains("expires_at", Assert.Single(handler.Seen).Body);
    }

    [LiveFact]
    public async Task FrameworkRecallUsesLiveExactIndexAndRechecksCurrentCaseAccess()
    {
        var endpoint = Environment.GetEnvironmentVariable("QDRANT_TEST_ENDPOINT")!;
        var uri = new Uri(endpoint);
        Assert.True(uri.IsLoopback, "The framework fixture requires a disposable loopback Qdrant instance.");
        var key = Environment.GetEnvironmentVariable("QDRANT_TEST_API_KEY") ?? "";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var collection = "ellai-framework-test-" + Guid.NewGuid().ToString("N");
        var index = new EllaiMemoryIndex(http, endpoint, key, collection);
        try
        {
            Assert.True(await index.EnsureExactCollectionAsync(CancellationToken.None));
            var topicsStore = new InMemoryEllaiTopicStore();
            var topics = new EllaiTopicService(topicsStore);
            var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "fixture-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
            var row = new CaseQueueItem(Guid.NewGuid(), "UnderReview", "SZ", "fixture-owner", 1, DateTimeOffset.UtcNow);
            var queue = new FixtureQueue(row);
            var context = new EllaiToolContext("fixture-session", "fixture-epoch", "cases", null, 4, true,
                "consent/1", EllaiProcessingClassification.SyntheticDemo, Actor: actor,
                AllowedTools: new HashSet<string> { EllaiToolNames.Topic });
            await new EllaiCaseResultMemory(topics, queue).RememberAsync(context, [row], CancellationToken.None);
            var topic = Assert.Single(await topicsStore.ListAsync(context.SessionId, CancellationToken.None));
            var reference = new EllaiExactMemoryReference("case-results", context.SessionId, context.ScopeEpoch,
                topic.TopicId, EllaiCaseResultMemory.SourceVersion(topic.EntityRef!), DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.True(await index.UpsertExactAsync(reference, CancellationToken.None));
            Assert.NotNull(await index.FindExactAsync(reference.Id, reference.SessionId, reference.ScopeEpoch, CancellationToken.None));

            var consent = new EllaiConsentService();
            consent.Grant(context.SessionId, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
                EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
            var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue, topics: topics, referenceIndex: index);
            async Task<string> RecallAsync()
            {
                using var client = new RecallClient();
                var agent = new EllaiFrameworkAgent(client);
                EllaiToolRequest? invoked = null;
                EllaiToolResult? observed = null;
                var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "recall")], 300,
                    _ => Task.FromResult(true), async (toolContext, request, ct) =>
                    {
                        invoked = request;
                        return observed = await broker.InvokeAsync(toolContext, request, ct);
                    },
                    new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
                    revalidateStreamObservations: async ct =>
                    {
                        if (invoked is null || observed is null) return false;
                        var current = await broker.InvokeAsync(context, invoked, ct);
                        return current.Allowed && current.ResultSummary == observed.ResultSummary &&
                            current.Evidence.SequenceEqual(observed.Evidence);
                    });
                Assert.NotNull(decision);
                return client.Observation;
            }

            Assert.Contains(row.CaseId.ToString("D"), await RecallAsync());
            queue.Permitted = false;
            Assert.DoesNotContain(row.CaseId.ToString("D"), await RecallAsync());
            queue.Permitted = true;
            Assert.True(await index.UpsertExactAsync(reference with { SourceVersion = "stale" }, CancellationToken.None));
            Assert.Contains(row.CaseId.ToString("D"), await RecallAsync());
        }
        finally
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, endpoint.TrimEnd('/') + "/collections/" + collection + "-exact");
            if (!string.IsNullOrWhiteSpace(key)) delete.Headers.TryAddWithoutValidation("api-key", key);
            using var result = await http.SendAsync(delete);
            Assert.True(result.IsSuccessStatusCode);
        }
    }

    private sealed class FixtureQueue(CaseQueueItem row) : ICaseQueueQuery
    {
        public bool Permitted { get; set; } = true;
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) =>
            Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, Permitted ? [row] : []));
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct) =>
            GetMineAsync(actor, limit, ct);
    }

    private sealed class RecallClient : IChatClient
    {
        private int _calls;
        public string Observation { get; private set; } = "";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call", EllaiToolNames.Topic, new Dictionary<string, object?> { ["ref"] = "case-results" })])));
            Observation = string.Join(" ", messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(content => content.Result?.ToString()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [LiveFact]
    public async Task LiveExactRoundTripUsesNoVectorsAndIsolatesScope()
    {
        var endpoint = Environment.GetEnvironmentVariable("QDRANT_TEST_ENDPOINT")!;
        var key = Environment.GetEnvironmentVariable("QDRANT_TEST_API_KEY") ?? "";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var collection = "ellai-exact-test-" + Guid.NewGuid().ToString("N");
        var index = new EllaiMemoryIndex(http, endpoint, key, collection);
        try
        {
            Assert.True(await index.EnsureExactCollectionAsync(CancellationToken.None));
            var reference = new EllaiExactMemoryReference("m", "s", "e", "topic", "version-1", DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.True(await index.UpsertExactAsync(reference, CancellationToken.None));
            Assert.Equal("version-1", (await index.FindExactAsync("m", "s", "e", CancellationToken.None))?.SourceVersion);
            Assert.Null(await index.FindExactAsync("m", "other", "e", CancellationToken.None));
            Assert.Null(await index.FindExactAsync("m", "s", "other", CancellationToken.None));
            Assert.True(await index.UpsertExactAsync(reference with { SessionId = "other", SourceVersion = "other-version" }, CancellationToken.None));
            Assert.Equal("version-1", (await index.FindExactAsync("m", "s", "e", CancellationToken.None))?.SourceVersion);
            Assert.True(await index.UpsertExactAsync(reference with { SourceVersion = "version-2" }, CancellationToken.None));
            Assert.Equal("version-2", (await index.FindExactAsync("m", "s", "e", CancellationToken.None))?.SourceVersion);
            Assert.False(await index.UpsertExactAsync(reference with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, CancellationToken.None));
        }
        finally
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, endpoint.TrimEnd('/') + "/collections/" + collection + "-exact");
            delete.Headers.TryAddWithoutValidation("api-key", key);
            using var result = await http.SendAsync(delete);
            Assert.True(result.IsSuccessStatusCode);
        }
    }

    [Fact]
    public async Task MissingEndpointDisablesWithoutCalling()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var index = new EllaiMemoryIndex(new HttpClient(handler), string.Empty, string.Empty, "ellai-memory");

        Assert.False(await index.EnsureCollectionAsync(128, CancellationToken.None));
        Assert.False(await index.UpsertAsync(
            new EllaiMemoryPoint("m", "s", "e", "t", "x", Array.Empty<string>(), DateTimeOffset.UtcNow),
            new[] { 0.1f }, CancellationToken.None));
        Assert.Empty(await index.SearchAsync(new[] { 0.1f }, "s", "e", 5, CancellationToken.None));
        Assert.Empty(handler.Seen);
    }
}
