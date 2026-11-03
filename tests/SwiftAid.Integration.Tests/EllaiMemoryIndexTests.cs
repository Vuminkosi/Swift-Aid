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

namespace SwiftAid.Integration.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T09")]
public sealed class EllaiMemoryIndexTests
{
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
                var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "recall")], 300,
                    _ => Task.FromResult(true), broker.InvokeAsync,
                    new EllaiFrameworkUsage(sessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None);
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
                var decision = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "recall")], 300,
                    _ => Task.FromResult(true), broker.InvokeAsync,
                    new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None);
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
