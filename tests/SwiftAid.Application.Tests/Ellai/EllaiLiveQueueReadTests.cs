using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Cases;
using Microsoft.Extensions.AI;
using SwiftAid.Intelligence.Meta;
using System.Runtime.CompilerServices;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T07")]
public sealed class EllaiLiveQueueReadTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true, "consent/1", EllaiProcessingClassification.SyntheticDemo);

    private sealed class FakeQueue : ICaseQueueQuery
    {
        public CaseQueueResult Result { get; set; } =
            new(CaseQueueAccess.Allowed, Array.Empty<CaseQueueItem>());

        public bool Throw { get; set; }

        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct)
        {
            LastActor = actor;
            LastLimit = limit;
            if (Throw)
            {
                throw new InvalidOperationException("store down");
            }

            return Task.FromResult(Result);
        }

        public CaseQueueFilter? LastFilter { get; private set; }

        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            return GetMineAsync(actor, limit, ct);
        }

        public ActorContext? LastActor { get; private set; }

        public int? LastLimit { get; private set; }
    }

    private sealed class FakeIndex(EllaiExactMemoryReference? hit, bool fail = false) : IEllaiExactReferenceIndex
    {
        public int Reads { get; private set; }
        public Task<EllaiExactMemoryReference?> FindExactAsync(string id, string session, string epoch, CancellationToken ct)
        {
            Reads++;
            if (fail) throw new InvalidOperationException("cache offline");
            return Task.FromResult(hit);
        }
    }

    private sealed class LateIndex(EllaiExactMemoryReference hit) : IEllaiExactReferenceIndex
    {
        public async Task<EllaiExactMemoryReference?> FindExactAsync(string id, string session, string epoch, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            return hit;
        }
    }

    private sealed class CountingTopicStore : IEllaiTopicStore
    {
        private readonly InMemoryEllaiTopicStore _inner = new();
        public int DirectReads { get; private set; }
        public Task<EllaiTopicEntry> RecordAsync(string sessionId, string title, IReadOnlyList<string> aliases,
            string? entityRef, string? entityVersion, string? checkpointId, DateTimeOffset now, CancellationToken ct) =>
            _inner.RecordAsync(sessionId, title, aliases, entityRef, entityVersion, checkpointId, now, ct);
        public Task<IReadOnlyList<EllaiTopicEntry>> ListAsync(string sessionId, CancellationToken ct) => _inner.ListAsync(sessionId, ct);
        public async Task<EllaiTopicEntry?> GetAsync(string sessionId, string topicId, CancellationToken ct)
        {
            DirectReads++;
            return (await _inner.ListAsync(sessionId, ct)).SingleOrDefault(item => item.TopicId == topicId);
        }
    }

    private sealed class LateTopicStore : IEllaiTopicStore
    {
        private readonly InMemoryEllaiTopicStore _inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EllaiTopicEntry> RecordAsync(string sessionId, string title, IReadOnlyList<string> aliases,
            string? entityRef, string? entityVersion, string? checkpointId, DateTimeOffset now, CancellationToken ct) =>
            _inner.RecordAsync(sessionId, title, aliases, entityRef, entityVersion, checkpointId, now, ct);
        public async Task<IReadOnlyList<EllaiTopicEntry>> ListAsync(string sessionId, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task;
            return await _inner.ListAsync(sessionId, ct);
        }
    }

    [Fact]
    public async Task CancelledQueueReadDoesNotWriteCaseRecallWhenDependencyReturnsLate()
    {
        var queue = new LateQueue();
        var topicsStore = new InMemoryEllaiTopicStore();
        var topics = new EllaiTopicService(topicsStore);
        var consent = new EllaiConsentService();
        consent.Grant(Context.SessionId, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue, topics: topics);
        var context = Context with { Actor = TestActor(), AllowedTools = new HashSet<string> { EllaiToolNames.State } };
        using var cancellation = new CancellationTokenSource();
        var run = broker.InvokeAsync(context, new EllaiToolRequest(EllaiToolNames.State,
            new Dictionary<string, string> { ["projection"] = "queue" }), cancellation.Token);
        await queue.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        queue.Complete.TrySetResult(new CaseQueueResult(CaseQueueAccess.Allowed, [Row("01", 7)]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await topicsStore.ListAsync(context.SessionId, default));
    }

    private sealed class LateQueue : ICaseQueueQuery
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CaseQueueResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct)
        {
            ct.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            return Complete.Task;
        }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct) =>
            GetMineAsync(actor, limit, ct);
    }

    [Fact]
    public async Task FrameworkDeadlinePreventsLateQueueResultFromWritingRecall()
    {
        var queue = new LateQueue();
        var topicsStore = new InMemoryEllaiTopicStore();
        var consent = new EllaiConsentService();
        consent.Grant(Context.SessionId, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue,
            topics: new EllaiTopicService(topicsStore));
        using var client = new QueueCallClient();
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("policy", "test"), broker,
            new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), TimeSpan.FromMilliseconds(100));
        var context = Context with { Actor = TestActor(), AllowedTools = new HashSet<string> { EllaiToolNames.State } };
        var run = executor.RunAsync(new EllaiLoopRequest(context, "show queue", new EllaiRoleOverlay("ask", "help"),
            "queue", 100, "cycle", 1), default);
        await queue.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Complete.TrySetResult(new CaseQueueResult(CaseQueueAccess.Allowed, [Row("01", 7)]));
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outcome.Completed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, outcome.FailureCode);
        Assert.Empty(await topicsStore.ListAsync(context.SessionId, default));
    }

    private sealed class QueueCallClient : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(++_calls == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", EllaiToolNames.State,
                    new Dictionary<string, object?> { ["projection"] = "queue" })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task FrameworkRecallRechecksScopeAndFallsBackOnStaleOrUnavailableIndex(bool stale, bool outage, bool wrongScope)
    {
        var topicsStore = new InMemoryEllaiTopicStore();
        var topics = new EllaiTopicService(topicsStore);
        var queue = new FakeQueue { Result = new(CaseQueueAccess.Allowed, [Row("01", 7)]) };
        var owner = Context with { Actor = TestActor(), AllowedTools = new HashSet<string> { EllaiToolNames.Topic } };
        await new EllaiCaseResultMemory(topics, queue).RememberAsync(owner, queue.Result.Items, CancellationToken.None);
        var entry = Assert.Single(await topicsStore.ListAsync(owner.SessionId, CancellationToken.None));
        var index = new FakeIndex(new("case-results", owner.SessionId, owner.ScopeEpoch, entry.TopicId,
            stale ? "stale-version" : EllaiCaseResultMemory.SourceVersion(entry.EntityRef!), DateTimeOffset.UtcNow.AddMinutes(5)), outage);
        var consent = new EllaiConsentService();
        consent.Grant(owner.SessionId, EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue, topics: topics, referenceIndex: index);
        using var client = new RecallClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = wrongScope ? owner with { ScopeEpoch = "other-epoch" } : owner;
        EllaiToolRequest? invoked = null;
        EllaiToolResult? observed = null;
        var result = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "recall")], 300,
            _ => Task.FromResult(true), async (toolContext, request, ct) =>
            {
                invoked = request;
                return observed = await broker.InvokeAsync(toolContext, request, ct);
            },
            new EllaiFrameworkUsage(owner.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
            revalidateStreamObservations: async ct =>
            {
                if (invoked is null || observed is null) return false;
                var current = await broker.InvokeAsync(context, invoked, ct);
                return current.Allowed && current.ResultSummary == observed.ResultSummary &&
                    current.Evidence.SequenceEqual(observed.Evidence);
            });

        Assert.Equal(!wrongScope, result is not null);
        if (!wrongScope) Assert.Equal(2, index.Reads);
        else Assert.InRange(index.Reads, 1, 2);
        if (!wrongScope) Assert.Contains(Row("01", 7).CaseId.ToString("D"), client.Observation);
        else Assert.DoesNotContain(Row("01", 7).CaseId.ToString("D"), client.Observation);
    }

    private sealed class RecallClient : IChatClient
    {
        private int _calls;
        public string Observation { get; private set; } = string.Empty;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call", EllaiToolNames.Topic, new Dictionary<string, object?> { ["ref"] = "case-results" })])));
            Observation = string.Join(" ", messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                .Select(item => item.Result?.ToString()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new NotSupportedException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ActorContext TestActor() => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "actor-1"),
            new Claim(ActorContext.AccessEpochClaimType, "3"),
        }, "test")));

    private static EllaiToolBroker GrantedBroker(ICaseQueueQuery? queue = null, EllaiTopicService? topics = null)
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent, null, queue, topics: topics);
    }

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static CaseQueueItem Row(string suffix, long version) => new(
        Guid.Parse($"11111111-1111-1111-1111-1111111111{suffix}"),
        "UnderReview", "US", "actor-1", version,
        new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task QueueProjectionReturnsLiveVersionedRows()
    {
        var queue = new FakeQueue
        {
            Result = new CaseQueueResult(CaseQueueAccess.Allowed,
                new[] { Row("01", 7), Row("02", 9) })
        };
        var broker = GrantedBroker(queue);
        var context = Context with { Actor = TestActor() };

        var result = await broker.InvokeAsync(
            context, new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(2, result.ItemCount);
        Assert.False(result.Truncated);
        Assert.Contains("v7", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("v9", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("country US", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("Owned cases only", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("review status is not supplied", result.ResultSummary, StringComparison.Ordinal);
        Assert.Equal("actor-1", queue.LastActor?.ActorId);
        Assert.Equal(
            new[] { "7", "9" },
            result.Evidence.Select(e => e.Version).ToArray());
        Assert.True(EllaiToolBroker.MeasureEnvelopeBytes(result) <= EllaiToolBroker.MaxResultBytes);
    }

    [Fact]
    public async Task QueueFiltersReachAuthorizedQueryBeforeLimiting()
    {
        var queue = new FakeQueue();
        var result = await GrantedBroker(queue).InvokeAsync(Context with { Actor = TestActor() },
            new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue"), ("country", "ZW"),
                ("workflow", "Reported"), ("sort", "latest-reported"), ("limit", "5"))), CancellationToken.None);
        Assert.True(result.Allowed);
        Assert.Equal(new CaseQueueFilter("ZW", "Reported", true), queue.LastFilter);
        Assert.Equal(5, queue.LastLimit);
        Assert.Equal("actor-1", queue.LastActor?.ActorId);
    }

    [Theory]
    [InlineData("country", "Zimbabwe")]
    [InlineData("workflow", "Unreviewed")]
    [InlineData("sort", "risk")]
    [InlineData("reviewStatus", "Unreviewed")]
    public async Task UnsupportedFiltersDoNotSilentlyReturnUnfilteredCases(string key, string value)
    {
        var queue = new FakeQueue();
        var result = await GrantedBroker(queue).InvokeAsync(Context with { Actor = TestActor() },
            new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue"), (key, value))), CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, result.ReasonCode);
        Assert.Null(queue.LastActor);
    }

    [Fact]
    public async Task ResultRecallReauthorizesIdsAndUsesCurrentVersions()
    {
        var topics = new EllaiTopicService();
        var queue = new FakeQueue { Result = new(CaseQueueAccess.Allowed, [Row("01", 7), Row("02", 8)]) };
        var context = Context with { Actor = TestActor() };
        await GrantedBroker(queue, topics).InvokeAsync(context,
            new(EllaiToolNames.State, Args(("projection", "queue"))), CancellationToken.None);
        // One record is no longer authorized; another changed. An unrelated newly returned row must not leak in.
        queue.Result = new(CaseQueueAccess.Allowed, [Row("01", 9), Row("03", 1)]);
        var recalled = await GrantedBroker(queue, topics).InvokeAsync(context,
            new(EllaiToolNames.Topic, Args(("ref", "case-results"))), CancellationToken.None);
        Assert.True(recalled.Allowed);
        Assert.True(recalled.HistoryValidUntilUtc > DateTimeOffset.UtcNow);
        Assert.DoesNotContain("HistoryValidUntilUtc", System.Text.Json.JsonSerializer.Serialize(recalled), StringComparison.OrdinalIgnoreCase);
        var observed = EllaiObservedToolResult.From(new EllaiToolRequest(EllaiToolNames.Topic, Args(("ref", "case-results"))), recalled);
        Assert.Equal(recalled.HistoryValidUntilUtc, observed.CaseSnapshotExpiresAtUtc);
        Assert.Equal(1, recalled.ItemCount);
        Assert.Equal("9", Assert.Single(recalled.Evidence).Version);
        Assert.Contains("changed since retrieval", recalled.ResultSummary);
        Assert.DoesNotContain(Row("02", 8).CaseId.ToString(), recalled.ResultSummary);
        Assert.DoesNotContain(Row("03", 1).CaseId.ToString(), recalled.ResultSummary);
        Assert.Equal(new[] { Row("01", 7).CaseId, Row("02", 8).CaseId }, queue.LastFilter!.CaseIds);
    }

    [Fact]
    public async Task EvidenceToolResolvesOnlyRetainedCaseResultsWithFreshAuthorizedFields()
    {
        var topics = new EllaiTopicService();
        var queue = new FakeQueue { Result = new(CaseQueueAccess.Allowed, [Row("01", 7)]) };
        var context = Context with { Actor = TestActor() };
        var broker = GrantedBroker(queue, topics);
        await broker.InvokeAsync(context,
            new(EllaiToolNames.State, Args(("projection", "queue"))), CancellationToken.None);

        queue.Result = new(CaseQueueAccess.Allowed, [Row("01", 9)]);
        var result = await broker.InvokeAsync(context,
            new(EllaiToolNames.Evidence, Args(("ref", "case-results"), ("field", "workflow"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(EllaiToolNames.Evidence, result.ToolName);
        Assert.Equal("workflow", Assert.Single(result.Evidence).FieldPath);
        Assert.Equal("9", Assert.Single(result.Evidence).Version);
        Assert.Contains("workflow UnderReview", result.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("country US", result.ResultSummary, StringComparison.Ordinal);
        Assert.Equal("actor-1", queue.LastActor?.ActorId);
        Assert.Equal(new[] { Row("01", 7).CaseId }, queue.LastFilter!.CaseIds);

        var unsupported = await broker.InvokeAsync(context,
            new(EllaiToolNames.Evidence, Args(("ref", "arbitrary-case-id"))), CancellationToken.None);
        Assert.False(unsupported.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, unsupported.ReasonCode);

        queue.Throw = true;
        var unavailable = await broker.InvokeAsync(context,
            new(EllaiToolNames.Evidence, Args(("ref", "case-results"))), CancellationToken.None);
        Assert.False(unavailable.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, unavailable.ReasonCode);
    }

    [Fact]
    public async Task ResultSnapshotsCannotBeReadThroughGenericTopicLookupOrChangedScope()
    {
        var topics = new EllaiTopicService();
        var queue = new FakeQueue { Result = new(CaseQueueAccess.Allowed, [Row("01", 7)]) };
        var context = Context with { Actor = TestActor() };
        var broker = GrantedBroker(queue, topics);
        await broker.InvokeAsync(context, new(EllaiToolNames.State, Args(("projection", "queue"))), CancellationToken.None);
        foreach (var reference in new[] { "the first issue", "latest case", "Ellai" })
        {
            var generic = await broker.InvokeAsync(context,
                new(EllaiToolNames.Topic, Args(("ref", reference))), CancellationToken.None);
            Assert.Equal(0, generic.ItemCount);
            Assert.DoesNotContain(Row("01", 7).CaseId.ToString(), generic.ResultSummary);
        }
        var memory = new EllaiCaseResultMemory(topics, queue);
        Assert.False((await memory.RecallAsync(context with { ScopeEpoch = "changed" }, CancellationToken.None)).Allowed);
        Assert.False((await memory.RecallAsync(context with { SessionId = "other" }, CancellationToken.None)).Allowed);
        Assert.False((await memory.RecallAsync(context with { Actor = null }, CancellationToken.None)).Allowed);
    }

    [Theory]
    [InlineData("other-actor", 24)]
    [InlineData("actor-1", -1)]
    public async Task ResultRecallRejectsOtherOwnersAndExpiredSnapshots(string actorId, int expiresInHours)
    {
        var topics = new EllaiTopicService();
        var queue = new FakeQueue();
        var snapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            ActorId = actorId, ScopeEpoch = Context.ScopeEpoch,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(expiresInHours),
            Cases = new[] { new { Id = Row("01", 7).CaseId, Version = 7L } }
        });
        await topics.TrackAsync(Context.SessionId, EllaiCaseResultMemory.TopicTitle, ["case-results"],
            snapshot, "case-results/1");
        var recalled = await new EllaiCaseResultMemory(topics, queue).RecallAsync(
            Context with { Actor = TestActor() }, CancellationToken.None);
        Assert.False(recalled.Allowed);
        Assert.Empty(recalled.Evidence);
        Assert.Null(queue.LastActor);
    }

    [Fact]
    public async Task ResultRecallDoesNotPresentAnOutageAsAnEmptyResult()
    {
        var topics = new EllaiTopicService();
        var queue = new FakeQueue { Result = new(CaseQueueAccess.Allowed, [Row("01", 7)]) };
        var context = Context with { Actor = TestActor() };
        var memory = new EllaiCaseResultMemory(topics, queue);
        await memory.RememberAsync(context, queue.Result.Items, CancellationToken.None);
        queue.Result = new(CaseQueueAccess.Unavailable, []);
        var recalled = await memory.RecallAsync(context, CancellationToken.None);
        Assert.False(recalled.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, recalled.ReasonCode);
        Assert.Empty(recalled.Evidence);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DerivedIndexNeverOverridesAuthoritativeResultReferences(bool stale, bool outage)
    {
        var store = new InMemoryEllaiTopicStore();
        var topics = new EllaiTopicService(store);
        var queue = new FakeQueue { Result = new(CaseQueueAccess.Allowed, [Row("01", 7)]) };
        var context = Context with { Actor = TestActor() };
        await new EllaiCaseResultMemory(topics, queue).RememberAsync(context, queue.Result.Items, CancellationToken.None);
        var entry = Assert.Single(await store.ListAsync(context.SessionId, CancellationToken.None));
        var index = new FakeIndex(new("case-results", context.SessionId, context.ScopeEpoch, entry.TopicId,
            stale ? "obsolete" : EllaiCaseResultMemory.SourceVersion(entry.EntityRef!), DateTimeOffset.UtcNow.AddHours(1)), outage);
        var recalled = await new EllaiCaseResultMemory(topics, queue, index).RecallAsync(context, CancellationToken.None);
        Assert.True(recalled.Allowed);
        Assert.Equal(1, index.Reads);
        Assert.Equal(Row("01", 7).CaseId.ToString("D"), Assert.Single(recalled.Evidence).ResourceId);
        Assert.NotNull(queue.LastFilter?.CaseIds);
    }

    [Fact]
    public async Task OverdueIndexHitFallsBackToAuthoritativeTopicLookup()
    {
        var store = new CountingTopicStore();
        var topics = new EllaiTopicService(store);
        var queue = new FakeQueue { Result = new(CaseQueueAccess.Allowed, [Row("01", 7)]) };
        var context = Context with { Actor = TestActor() };
        await new EllaiCaseResultMemory(topics, queue).RememberAsync(context, queue.Result.Items, default);
        var entry = Assert.Single(await store.ListAsync(context.SessionId, default));
        var hit = new EllaiExactMemoryReference("case-results", context.SessionId, context.ScopeEpoch,
            entry.TopicId, EllaiCaseResultMemory.SourceVersion(entry.EntityRef!), DateTimeOffset.UtcNow.AddHours(1));

        var recalled = await new EllaiCaseResultMemory(topics, queue, new LateIndex(hit)).RecallAsync(context, default);

        Assert.True(recalled.Allowed);
        Assert.Equal(0, store.DirectReads);
        Assert.Equal(Row("01", 7).CaseId.ToString("D"), Assert.Single(recalled.Evidence).ResourceId);
    }

    [Fact]
    public async Task CancelledRecallRejectsLateQueueResult()
    {
        var topics = new EllaiTopicService();
        var queue = new LateQueue();
        var context = Context with { Actor = TestActor() };
        var memory = new EllaiCaseResultMemory(topics, queue);
        await memory.RememberAsync(context, [Row("01", 7)], default);
        using var cancellation = new CancellationTokenSource();
        var recall = memory.RecallAsync(context, cancellation.Token);
        await queue.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        queue.Complete.TrySetResult(new CaseQueueResult(CaseQueueAccess.Allowed, [Row("01", 7)]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recall.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancelledRecallRejectsLateEmptyTopicResult()
    {
        var store = new LateTopicStore();
        var memory = new EllaiCaseResultMemory(new EllaiTopicService(store), new FakeQueue());
        var context = Context with { Actor = TestActor() };
        await memory.RememberAsync(context, [], default);
        using var cancellation = new CancellationTokenSource();
        var recall = memory.RecallAsync(context, cancellation.Token);
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        store.Release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recall.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task EmptySearchReplacesPreviousResultSet()
    {
        var topics = new EllaiTopicService();
        var queue = new FakeQueue { Result = new(CaseQueueAccess.Allowed, [Row("01", 7)]) };
        var context = Context with { Actor = TestActor() };
        var broker = GrantedBroker(queue, topics);
        var search = new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue")));
        await broker.InvokeAsync(context, search, CancellationToken.None);
        queue.Result = new(CaseQueueAccess.Allowed, []);
        await broker.InvokeAsync(context, search, CancellationToken.None);
        var recalled = await broker.InvokeAsync(context,
            new(EllaiToolNames.Topic, Args(("ref", "case-results"))), CancellationToken.None);
        Assert.True(recalled.Allowed);
        Assert.Empty(recalled.Evidence);
        Assert.Contains("was empty", recalled.ResultSummary);
    }

    [Fact]
    public async Task ChangedSourceShowsNewVersionOnReread()
    {
        var queue = new FakeQueue
        {
            Result = new CaseQueueResult(CaseQueueAccess.Allowed, new[] { Row("01", 7) })
        };
        var broker = GrantedBroker(queue);
        var context = Context with { Actor = TestActor() };
        var request = new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue")));

        var first = await broker.InvokeAsync(context, request, CancellationToken.None);
        Assert.Contains("v7", first.ResultSummary, StringComparison.Ordinal);

        queue.Result = new CaseQueueResult(CaseQueueAccess.Allowed, new[] { Row("01", 8) });
        var second = await broker.InvokeAsync(context, request, CancellationToken.None);
        Assert.Contains("v8", second.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("v7", second.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueOutageAndDenialAreExplicit()
    {
        var queue = new FakeQueue { Result = new CaseQueueResult(CaseQueueAccess.Unavailable, []) };
        var broker = GrantedBroker(queue);
        var context = Context with { Actor = TestActor() };
        var request = new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue")));

        var unavailable = await broker.InvokeAsync(context, request, CancellationToken.None);
        Assert.False(unavailable.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, unavailable.ReasonCode);

        queue.Result = new CaseQueueResult(CaseQueueAccess.Denied, []);
        var denied = await broker.InvokeAsync(context, request, CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, denied.ReasonCode);

        queue.Throw = true;
        var failed = await broker.InvokeAsync(context, request, CancellationToken.None);
        Assert.False(failed.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, failed.ReasonCode);
    }

    [Fact]
    public async Task MissingActorOrServiceYieldsUnavailableNeverFakeRows()
    {
        var queue = new FakeQueue
        {
            Result = new CaseQueueResult(CaseQueueAccess.Allowed, new[] { Row("01", 7) })
        };
        var request = new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue")));

        var noActor = await GrantedBroker(queue).InvokeAsync(Context, request, CancellationToken.None);
        Assert.False(noActor.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, noActor.ReasonCode);

        var noService = await GrantedBroker().InvokeAsync(
            Context with { Actor = TestActor() }, request, CancellationToken.None);
        Assert.False(noService.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, noService.ReasonCode);
    }

    [Fact]
    public async Task QueueProjectionBoundsAreEnforced()
    {
        var rows = Enumerable.Range(1, 8).Select(i => Row(i.ToString("D2"), i)).ToArray();
        var queue = new FakeQueue { Result = new CaseQueueResult(CaseQueueAccess.Allowed, rows) };
        var broker = GrantedBroker(queue);
        var context = Context with { Actor = TestActor() };

        var paged = await broker.InvokeAsync(
            context, new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue"), ("limit", "5"))),
            CancellationToken.None);
        Assert.True(paged.Allowed);
        Assert.Equal(5, paged.ItemCount);
        Assert.True(paged.Truncated);
        Assert.Equal(5, queue.LastLimit);

        var badLimit = await broker.InvokeAsync(
            context, new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "queue"), ("limit", "99"))),
            CancellationToken.None);
        Assert.False(badLimit.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, badLimit.ReasonCode);

        var unknown = await broker.InvokeAsync(
            context, new EllaiToolRequest(EllaiToolNames.State, Args(("projection", "ledger"))),
            CancellationToken.None);
        Assert.False(unknown.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, unknown.ReasonCode);

        var feature = await broker.InvokeAsync(
            context, new EllaiToolRequest(EllaiToolNames.State, Args()),
            CancellationToken.None);
        Assert.True(feature.Allowed);
        Assert.Equal("Current permitted feature projection.", feature.ResultSummary);
        var observation = EllaiObservedToolResult.From(new EllaiToolRequest(EllaiToolNames.State, Args()), feature);
        Assert.Equal(EllaiObservedToolResult.CurrentStaticFeatureVersion, observation.StaticFeatureVersion);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var history = new[]
        {
            new EllaiConversationTurn("session-1", "previous", 1, "question", "The feature projection was checked.", "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace },
            new EllaiConversationTurn("session-1", "current", 2, "follow-up", null, "Queued", 0, null)
        };
        var replay = await EllaiAuthorizedConversationContext.BuildAsync(history, "current", TestActor(), null, CancellationToken.None);
        Assert.Contains(replay.Messages, item => item.Content == "The feature projection was checked.");
        var unmarkedObservation = observation with { StaticFeatureVersion = null };
        var oldTrace = EllaiToolTrace.SerializeFlattened([unmarkedObservation], false, []);
        var omitted = await EllaiAuthorizedConversationContext.BuildAsync([history[0] with { ToolTraceJson = oldTrace }, history[1]],
            "current", TestActor(), null, CancellationToken.None);
        Assert.DoesNotContain(omitted.Messages, item => item.Content == "The feature projection was checked.");
    }
}
