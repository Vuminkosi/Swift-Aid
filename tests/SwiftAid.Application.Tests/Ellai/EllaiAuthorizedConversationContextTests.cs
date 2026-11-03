using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;
using Microsoft.Extensions.AI;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiAuthorizedConversationContextTests
{
    [Fact]
    public async Task PartialRetainedResultHistoryRequiresIdenticalCurrentlyAuthorizedRecall()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var queue = new BatchQueue();
        var topics = new EllaiTopicService();
        var actor = Actor();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: actor,
            AllowedTools: new HashSet<string> { EllaiToolNames.Evidence });
        var id = Guid.NewGuid();
        var row = new CaseQueueItem(id, "Reported", "SZ", actor.ActorId, 3, DateTimeOffset.UtcNow);
        var memory = new EllaiCaseResultMemory(topics, queue);
        await memory.RememberAsync(context, [row], default, searchIncomplete: true);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue, topics: topics);
        var request = new EllaiToolRequest(EllaiToolNames.Evidence, new Dictionary<string, string> { ["ref"] = "case-results" });
        var result = await broker.InvokeAsync(context, request, default);
        var observation = EllaiObservedToolResult.From(request, result);
        Assert.True(result.Truncated);
        Assert.NotNull(observation.RetainedCaseReplay);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        Assert.NotNull(trace);
        Assert.DoesNotContain(result.ResultSummary, trace);
        var turns = new[] { Turn("prior", 1, "cases?", "partial case answer", trace),
            Turn("current", 2, "follow-up", null, null) };
        async Task<bool> Retained(EllaiToolContext currentContext) =>
            (await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", actor, queue, default,
                toolContext: currentContext, invokeTool: broker.InvokeAsync)).Messages.Any(message => message.Content == "partial case answer");

        Assert.True(await Retained(context));
        Assert.False(await Retained(context with { Actor = Actor("other") }));
        Assert.False(await Retained(context with { AllowedTools = new HashSet<string>() }));
        queue.Revoked = id;
        Assert.False(await Retained(context));
        queue.Revoked = null;
        await memory.RememberAsync(context, [], default, searchIncomplete: true);
        Assert.False(await Retained(context));
    }

    [Fact]
    public async Task LongAuthorizedHistoryIsNotDiscardedAtFormerCharacterThreshold()
    {
        var trace = EllaiToolTrace.Serialize([], false);
        var longAnswer = new string('x', EllaiConversationContext.MaximumCharacters + 1);
        var turns = new[]
        {
            Turn("older", 1, "remember cobalt", "cobalt", trace),
            Turn("long", 2, "long response", longAnswer, trace),
            Turn("current", 3, "what word?", null, null)
        };

        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);

        Assert.Equal(2, result.RetainedTurnIds.Count);
        Assert.Contains(result.Messages, message => message.Content == "cobalt");
        Assert.Contains(result.Messages, message => message.Content == longAnswer);
    }

    [Fact]
    public async Task RetainsMoreThanSixSmallAuthorizedTurnsWithinContextWindow()
    {
        var trace = EllaiToolTrace.Serialize([], false);
        var turns = Enumerable.Range(1, 12).Select(index =>
            Turn($"turn-{index}", index, $"question-{index}", $"answer-{index}", trace)).ToList();
        turns.Add(Turn("current", 13, "follow-up", null, null));

        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);

        Assert.Equal(12, result.RetainedTurnIds.Count);
        Assert.Contains(result.Messages, message => message.Content == "answer-1");
        Assert.Contains(result.Messages, message => message.Content == "answer-12");
    }

    [Fact]
    public async Task OlderRetainedCaseTurnStillRequiresCurrentAuthorization()
    {
        var caseId = Guid.NewGuid();
        var observation = new EllaiObservedToolResult(EllaiToolNames.Topic, true, "Ok",
            [new EvidenceRef("CaseQueue", caseId.ToString("D"), "3", null, "proof")], false,
            CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        var turns = new List<EllaiConversationTurn>
        {
            Turn("case-turn", 1, "private question", "private answer", EllaiToolTrace.SerializeFlattened([observation], false, []))
        };
        var trace = EllaiToolTrace.Serialize([], false);
        turns.AddRange(Enumerable.Range(2, 10).Select(index => Turn($"turn-{index}", index, "question", "answer", trace)));
        turns.Add(Turn("current", 12, "follow-up", null, null));
        var queue = new BatchQueue();

        var authorized = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.Contains(authorized.Messages, message => message.Content == "private answer");
        queue.Revoked = caseId;
        var revoked = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.DoesNotContain(revoked.Messages, message => message.Content == "private answer");
    }
    [Fact]
    public async Task RunnerStopsBeforeModelWhenCurrentTurnIsMissingFromOutboxHistory()
    {
        var executor = new CapturingExecutor();
        var work = new EllaiUtteranceWork("session", "scope", "next", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", Actor(), true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose, ClientUtteranceId: "current");
        var otherSession = Turn("current", 1, "other", null, null) with { SessionId = "other" };

        var result = await EllaiUtteranceRunner.RunOneAsync(work,
            new(executor, null, null, new EllaiConsentService(), Outbox: new ConversationOutbox([otherSession])), 256, default);

        Assert.False(result.Started);
        Assert.False(result.Completed);
        Assert.Equal("HistoryUnavailable", result.FailureCode);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task LargeDistinctCaseHistoryRequiresEveryCurrentCaseGrant()
    {
        var ids = Enumerable.Range(0, 300).Select(_ => Guid.NewGuid()).ToArray();
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var separate = ids.Take(257).Select(id => new EllaiObservedToolResult(EllaiToolNames.Topic, true, "Ok",
            [new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, "proof")], false,
            CaseSnapshotExpiresAtUtc: expires)).ToArray();
        Assert.Null(EllaiToolTrace.SerializeFlattened(separate, false, []));
        var observations = ids.Chunk(20).Select(batch => new EllaiObservedToolResult(EllaiToolNames.Topic, true, "Ok",
            batch.Select(id => new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, "proof")).ToArray(), false,
            CaseSnapshotExpiresAtUtc: expires)).ToArray();
        var trace = EllaiToolTrace.SerializeFlattened(observations, false, []);
        Assert.NotNull(trace);
        var turns = new[] { Turn("prior", 1, "question", "authorized answer", trace),
            Turn("current", 2, "follow-up", null, null) };
        var queue = new BatchQueue();
        var authorized = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.Contains(authorized.Messages, message => message.Content == "authorized answer");
        Assert.Equal(15, queue.Calls);
        queue.Revoked = ids[^1];
        var revoked = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.DoesNotContain(revoked.Messages, message => message.Content == "authorized answer");
    }

    private sealed class BatchQueue : ICaseQueueQuery
    {
        public Guid? Revoked { get; set; }
        public int Calls { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct)
        {
            Calls++;
            var rows = filter.CaseIds!.Where(id => id != Revoked)
                .Select(id => new CaseQueueItem(id, "Reported", "SZ", actor.ActorId, 3, DateTimeOffset.UtcNow)).ToArray();
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, rows));
        }
    }

    [Fact]
    public async Task LateCaseAuthorizationCannotRestoreHistoryAfterCancellation()
    {
        var id = Guid.NewGuid();
        var observation = new EllaiObservedToolResult(EllaiToolNames.Topic, true, "Ok",
            [new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, "proof")], false,
            CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var turns = new[] { Turn("prior", 1, "question", "private case answer", trace),
            Turn("current", 2, "follow-up", null, null) };
        var queue = new LateAuthorizationQueue();
        using var cancellation = new CancellationTokenSource();
        var authorization = EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, cancellation.Token);
        await queue.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        queue.Release.TrySetResult(new CaseQueueResult(CaseQueueAccess.Allowed,
            [new CaseQueueItem(id, "Reported", "SZ", Actor().ActorId, 3, DateTimeOffset.UtcNow)]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorization.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class LateAuthorizationQueue : ICaseQueueQuery
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CaseQueueResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct)
        {
            Started.TrySetResult();
            return Release.Task;
        }
    }

    [Fact]
    public async Task RunnerDoesNotRestoreCaseHistoryWhenSessionToolAdmissionIsRemoved()
    {
        var id = Guid.NewGuid();
        var observation = new EllaiObservedToolResult(EllaiToolNames.Topic, true, "Ok",
            [new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, "proof")], false,
            CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var turns = new[] { Turn("prior", 1, "case question", "private case answer", trace),
            Turn("current", 2, "follow-up", null, null) };
        var executor = new CapturingExecutor();
        var queue = new BatchQueue();
        var work = new EllaiUtteranceWork("session", "scope", "next", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", Actor(), true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools: new HashSet<string>(), ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose,
            ClientUtteranceId: "current");
        await EllaiUtteranceRunner.RunOneAsync(work,
            new(executor, null, null, new EllaiConsentService(), Outbox: new ConversationOutbox(turns), CaseQueue: queue), 256, default);
        Assert.DoesNotContain(executor.Messages, message => message.Content == "private case answer");
        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task RunnerUsesWorkSessionWhenOutboxReturnsCollidingTurnIds()
    {
        var trace = EllaiToolTrace.Serialize([], false);
        var turns = new[]
        {
            Turn("prior-other", 1, "other question", "other answer", trace) with { SessionId = "other" },
            Turn("current", 2, "other current", null, null) with { SessionId = "other" },
            Turn("prior", 1, "own question", "own answer", trace),
            Turn("current", 2, "current", null, null)
        };
        var executor = new CapturingExecutor();
        var work = new EllaiUtteranceWork("session", "scope", "next", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", Actor(), true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose, ClientUtteranceId: "current");
        await EllaiUtteranceRunner.RunOneAsync(work,
            new(executor, null, null, new EllaiConsentService(), Outbox: new ConversationOutbox(turns)), 256, default);
        Assert.Contains(executor.Messages, message => message.Content == "own answer");
        Assert.DoesNotContain(executor.Messages, message => message.Content == "other answer");
    }

    private sealed class CapturingExecutor : IEllaiReasoningExecutor, IEllaiHistoryAuthorizationRequired
    {
        public int Calls { get; private set; }
        public EllaiLoopRequest? Request { get; private set; }
        public IReadOnlyList<EllaiChatMessage> Messages { get; private set; } = [];
        public Task<EllaiCycleOutcome> RunAsync(EllaiLoopRequest request, CancellationToken ct,
            Func<int, CancellationToken, Task<bool>>? reauthorizeAsync = null,
            EllaiActivityStatusCoordinator? activity = null, EllaiSessionEventJournal? journal = null,
            Func<CancellationToken, Task<bool>>? isExecutionCurrentAsync = null,
            Func<string, CancellationToken, ValueTask>? onTextDelta = null, Action<SwiftAid.Application.Ellai.EllaiActivityEvent>? onActivity = null,
            Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? invokeToolAsync = null)
        {
            Calls++;
            Request = request;
            Messages = request.Conversation ?? [];
            return Task.FromResult(new EllaiCycleOutcome(false, null, "test", 0, 0, false));
        }
    }

    [Fact]
    public async Task StreamObservationAuthorizationRechecksCaseAccess()
    {
        var executor = new CapturingExecutor();
        var queue = new BatchQueue();
        var caseId = Guid.NewGuid();
        var observation = new EllaiObservedToolResult(EllaiToolNames.Topic, true, "Ok",
            [new EvidenceRef("CaseQueue", caseId.ToString("D"), "3", null, "proof")], false,
            CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        var work = new EllaiUtteranceWork("session", "scope", "next", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", Actor(), true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools: new HashSet<string> { EllaiToolNames.Topic },
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose, ClientUtteranceId: "current");
        await EllaiUtteranceRunner.RunOneAsync(work,
            new(executor, null, null, new EllaiConsentService(), CaseQueue: queue,
                OnVerifiedStreamSegment: (_, _) => ValueTask.CompletedTask), 256, default);
        var check = Assert.IsType<Func<IReadOnlyList<EllaiObservedToolResult>, CancellationToken, Task<bool>>>(
            executor.Request!.RevalidateStreamObservations);
        Assert.True(await check([observation], default));
        queue.Revoked = caseId;
        Assert.False(await check([observation], default));
    }

    [Fact]
    public async Task MixedSessionRowsCannotEnterHistoryOrSatisfyDependencies()
    {
        var publicTrace = EllaiToolTrace.Serialize([], false);
        var dependentTrace = EllaiToolTrace.Serialize([], true, ["foreign"]);
        var turns = new[]
        {
            Turn("foreign", 1, "other question", "other answer", publicTrace) with { SessionId = "other-session" },
            Turn("dependent", 2, "question", "dependent answer", dependentTrace),
            Turn("current", 3, "current", null, null)
        };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
        Assert.DoesNotContain(result.Messages, message => message.Content == "other answer" || message.Content == "dependent answer");
        Assert.Empty(result.RetainedTurnIds);
    }

    [Theory]
    [InlineData("report-help", true)]
    [InlineData("arbitrary private phrase", false)]
    public async Task GoalFilteredCatalogueReplaysOnlyServerOwnedTags(string goal, bool expected)
    {
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), uiRegistry: registry);
        var context = new EllaiToolContext("session", "epoch", "public.product-help", null, 4, true,
            DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive, Actor: Actor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.UiCatalogue },
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
        var request = new EllaiToolRequest(EllaiToolNames.UiCatalogue, new Dictionary<string, string> { ["goal"] = goal });
        var result = await broker.InvokeAsync(context, request, default);
        Assert.True(result.Allowed);
        Assert.Equal(expected ? goal : null, result.UiCatalogueReplayScope?.GoalTag);
        var trace = EllaiToolTrace.SerializeFlattened([EllaiObservedToolResult.From(request, result)], false, []);
        Assert.DoesNotContain("arbitrary private phrase", trace!);
        var turns = new[] { Turn("prior", 1, "show report", "report guidance", trace),
            Turn("current", 2, "continue", null, null) };
        var replay = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default,
            toolContext: context, invokeTool: broker.InvokeAsync);
        Assert.Equal(expected, replay.Messages.Any(message => message.Content == "report guidance"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AggregateHistoryRequiresToolToRemainInSessionAllowlist(bool permitted)
    {
        var caseId = Guid.NewGuid();
        var observation = new EllaiObservedToolResult(EllaiToolNames.OwnedCaseCount, true, "Ok",
            [new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "evidence")], false,
            CountReplayScope: EllaiCountReplayScope.Create(30, [caseId]));
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var turns = new[] { Turn("prior", 1, "how many?", "one case", trace),
            Turn("current", 2, "repeat", null, null) };
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            Actor: Actor(), AllowedTools: permitted ? new HashSet<string> { EllaiToolNames.OwnedCaseCount } : new HashSet<string>());
        var replay = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default,
            dashboard: new AggregateQuery("same", caseId), toolContext: context);
        Assert.Equal(permitted, replay.Messages.Any(message => message.Content == "one case"));
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("changed", false)]
    [InlineData("denied", false)]
    [InlineData("other-actor", false)]
    [InlineData("outage", false)]
    public async Task CatalogueHistoryRequiresTheSameCurrentlyAuthorizedProjection(string state, bool expected)
    {
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var consent = new EllaiConsentService();
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, uiRegistry: registry);
        var context = new EllaiToolContext("session", "epoch", "public.product-help", null, 4, true,
            DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive, Actor: Actor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.UiCatalogue },
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
        var request = new EllaiToolRequest(EllaiToolNames.UiCatalogue, new Dictionary<string, string>());
        var original = await broker.InvokeAsync(context, request, default);
        Assert.True(original.Allowed);
        Assert.NotNull(original.UiCatalogueReplayScope);
        Assert.DoesNotContain("UiCatalogueReplayScope", System.Text.Json.JsonSerializer.Serialize(original));
        var observation = EllaiObservedToolResult.From(request, original);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var turns = new[] { Turn("prior", 1, "show report", "report guidance", trace),
            Turn("current", 2, "continue", null, null) };
        var currentBroker = state == "changed"
            ? new EllaiToolBroker(new EllaiPolicyService(), consent, uiRegistry: new EllaiUiRegistry()) : broker;
        var currentContext = state switch
        {
            "denied" => context with { AllowedTools = new HashSet<string>() },
            "other-actor" => context with { Actor = Actor("other") },
            _ => context
        };
        Task<EllaiToolResult> Invoke(EllaiToolContext toolContext, EllaiToolRequest tool, CancellationToken ct) =>
            state == "outage" ? throw new InvalidOperationException() : currentBroker.InvokeAsync(toolContext, tool, ct);

        var replay = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default,
            toolContext: currentContext, invokeTool: Invoke);
        Assert.Equal(expected, replay.Messages.Any(message => message.Content == "report guidance"));
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with { ToolName = EllaiToolNames.Now }], false, []));
    }

    [Fact]
    public async Task BrokeredAggregateWritesInternalReplayProvenance()
    {
        var caseId = Guid.NewGuid();
        var dashboard = new AggregateQuery("same", caseId);
        var consent = new EllaiConsentService();
        consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, dashboard: dashboard);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: Actor());
        var request = new EllaiToolRequest(EllaiToolNames.OwnedCaseCount,
            new Dictionary<string, string> { ["window_days"] = "30" });
        var result = await broker.InvokeAsync(context, request, CancellationToken.None);
        Assert.True(result.Allowed);
        Assert.DoesNotContain("CountReplayScope", System.Text.Json.JsonSerializer.Serialize(result));
        var observation = EllaiObservedToolResult.From(request, result);
        Assert.Equal(EllaiCountReplayScope.Create(30, [caseId]), observation.CountReplayScope);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var turns = new[] { Turn("prior", 1, "how many?", result.ResultSummary, trace),
            Turn("current", 2, "repeat", null, null) };
        var replay = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null,
            default, dashboard: dashboard);
        Assert.Contains(replay.Messages, message => message.Content == result.ResultSummary);
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("different", false)]
    [InlineData("denied", false)]
    [InlineData("truncated", false)]
    [InlineData("outage", false)]
    public async Task AggregateHistoryRequiresSameCurrentlyAuthorizedMembership(string state, bool expected)
    {
        var caseId = Guid.NewGuid();
        var scope = EllaiCountReplayScope.Create(30, [caseId]);
        var evidence = new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "evidence");
        var observation = new EllaiObservedToolResult(EllaiToolNames.OwnedCaseCount, true, "Ok", [evidence], false,
            CountReplayScope: scope);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var turns = new[] { Turn("prior", 1, "how many?", "one case", trace),
            Turn("current", 2, "what did you say?", null, null) };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default,
            dashboard: new AggregateQuery(state, caseId));

        Assert.Equal(expected, result.Messages.Any(message => message.Content == "one case"));
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with { ToolName = EllaiToolNames.Now }], false, []));
    }

    private sealed class AggregateQuery(string state, Guid caseId) : IDashboardAggregateQuery
    {
        public Task<DashboardAggregateResult> GetAsync(ActorContext actor, TimeSpan window, CancellationToken ct)
        {
            if (state == "outage") throw new InvalidOperationException();
            var row = new DashboardAggregateCaseRow(state == "different" ? Guid.NewGuid() : caseId,
                default, default, default, "SZ", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return Task.FromResult(new DashboardAggregateResult(state == "denied"
                ? DashboardAggregateAccess.Denied : DashboardAggregateAccess.Allowed, [row], DateTimeOffset.UtcNow,
                state == "truncated"));
        }
    }

    [Fact]
    public async Task ReceiptListHistoryRequiresTheSameAuthorizedSessionResult()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var receipts = new EllaiReceiptStore();
        var original = await receipts.RecordAsync("session", "action", "idempotency", "plan", 1,
            "private-recipient", null, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), "policy", default);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, receipts: receipts);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: Actor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.Receipts });
        var request = new EllaiToolRequest(EllaiToolNames.Receipts, new Dictionary<string, string>());
        var result = await broker.InvokeAsync(context, request, default);
        Assert.True(result.Allowed);
        var observation = EllaiObservedToolResult.From(request, result);
        Assert.NotNull(observation.ReceiptListDigest);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        Assert.DoesNotContain("private-recipient", trace!);
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with { ToolName = EllaiToolNames.Now }], false, []));
        Assert.Null(EllaiObservedToolResult.From(new EllaiToolRequest(EllaiToolNames.Receipts,
            new Dictionary<string, string> { ["receipt"] = "private arbitrary input" }), result).ReceiptListDigest);
        Assert.Null(EllaiObservedToolResult.From(request, result with { ItemCount = result.ItemCount + 1 }).ReceiptListDigest);
        var turns = new[] { Turn("prior", 1, "receipt?", "receipt answer", trace),
            Turn("current", 2, "what happened?", null, null) };
        async Task<bool> Retained(EllaiToolContext currentContext,
            Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke) =>
            (await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default,
                toolContext: currentContext, invokeTool: invoke)).Messages.Any(message => message.Content == "receipt answer");

        Assert.True(await Retained(context, broker.InvokeAsync));
        var mismatched = observation with { Evidence = [new EvidenceRef("Receipt", "different", "Accepted", null, "different")] };
        var forged = EllaiToolTrace.SerializeFlattened([observation, mismatched], false, []);
        var forgedTurns = new[] { Turn("prior", 1, "receipt?", "forged answer", forged),
            Turn("current", 2, "what happened?", null, null) };
        var forgedReplay = await EllaiAuthorizedConversationContext.BuildAsync(forgedTurns, "current", Actor(), null, default,
            toolContext: context, invokeTool: broker.InvokeAsync);
        Assert.DoesNotContain(forgedReplay.Messages, message => message.Content == "forged answer");
        Assert.False(await Retained(context with { Actor = Actor("other") }, broker.InvokeAsync));
        Assert.False(await Retained(context with { SessionId = "other-session" }, broker.InvokeAsync));
        Assert.False(await Retained(context with { AllowedTools = new HashSet<string>() }, broker.InvokeAsync));
        Assert.False(await Retained(context, (_, _, _) => throw new InvalidOperationException("outage")));
        await receipts.MarkStatusAsync(original.ReceiptId, EllaiReceiptStatus.Failed, DateTimeOffset.UtcNow, default);
        Assert.False(await Retained(context, broker.InvokeAsync));
    }

    [Fact]
    public async Task ReceiptLookupHistoryRequiresAnUnchangedAuthorizedResult()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var receipts = new EllaiReceiptStore();
        var original = await receipts.RecordAsync("session", "action", "idempotency", "plan", 1,
            "private-recipient", null, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), "policy", default);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, receipts: receipts);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: Actor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.Receipts });
        var request = new EllaiToolRequest(EllaiToolNames.Receipts,
            new Dictionary<string, string> { ["receipt"] = original.ReceiptId });
        var result = await broker.InvokeAsync(context, request, default);
        var observation = EllaiObservedToolResult.From(request, result);
        Assert.Equal(original.ReceiptId, observation.ReceiptLookupId);
        Assert.NotNull(observation.ReceiptListDigest);
        Assert.Null(EllaiObservedToolResult.From(request, result with { Allowed = false }).ReceiptLookupId);
        Assert.Null(EllaiObservedToolResult.From(request, result with { Truncated = true }).ReceiptLookupId);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        Assert.NotNull(trace);
        Assert.DoesNotContain("private-recipient", trace);
        var turns = new[] { Turn("prior", 1, "receipt?", "receipt answer", trace),
            Turn("current", 2, "what happened?", null, null) };
        async Task<bool> Retained(EllaiToolContext currentContext,
            Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke) =>
            (await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default,
                toolContext: currentContext, invokeTool: invoke)).Messages.Any(message => message.Content == "receipt answer");
        Assert.True(await Retained(context, broker.InvokeAsync));
        Assert.False(await Retained(context with { Actor = Actor("other") }, broker.InvokeAsync));
        Assert.False(await Retained(context with { SessionId = "other-session" }, broker.InvokeAsync));
        Assert.False(await Retained(context with { AllowedTools = new HashSet<string>() }, broker.InvokeAsync));
        Assert.False(await Retained(context, (_, _, _) => throw new InvalidOperationException("outage")));
        await receipts.MarkStatusAsync(original.ReceiptId, EllaiReceiptStatus.Failed, DateTimeOffset.UtcNow, default);
        Assert.False(await Retained(context, broker.InvokeAsync));
    }

    [Fact]
    public async Task EmptyReceiptListHistoryIsOmittedWhenReceiptAppears()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var receipts = new EllaiReceiptStore();
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, receipts: receipts);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: Actor(),
            AllowedTools: new HashSet<string> { EllaiToolNames.Receipts });
        var request = new EllaiToolRequest(EllaiToolNames.Receipts, new Dictionary<string, string>());
        var result = await broker.InvokeAsync(context, request, default);
        var trace = EllaiToolTrace.SerializeFlattened([EllaiObservedToolResult.From(request, result)], false, []);
        var turns = new[] { Turn("prior", 1, "receipts?", "none found", trace),
            Turn("current", 2, "what did you find?", null, null) };
        var before = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default,
            toolContext: context, invokeTool: broker.InvokeAsync);
        Assert.Contains(before.Messages, message => message.Content == "none found");
        await receipts.RecordAsync("session", "action", "idempotency", "plan", 1, null, null,
            DateTimeOffset.UtcNow, TimeSpan.FromHours(1), "policy", default);
        var after = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default,
            toolContext: context, invokeTool: broker.InvokeAsync);
        Assert.DoesNotContain(after.Messages, message => message.Content == "none found");
    }

    [Fact]
    public async Task FixedCapabilityDescriptorHistorySurvivesReplayButUnknownShapesDoNot()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo);
        var request = new EllaiToolRequest(EllaiToolNames.Capabilities, new Dictionary<string, string>());
        var result = await broker.InvokeAsync(context, request, default);
        Assert.True(result.Allowed);
        var observation = EllaiObservedToolResult.From(request, result);
        Assert.Equal(EllaiObservedToolResult.CurrentCapabilityVersion, observation.CapabilityVersion);
        Assert.Null(EllaiObservedToolResult.From(request, result with { ResultSummary = "Changed descriptor" }).CapabilityVersion);
        Assert.Null(EllaiObservedToolResult.From(request, result with { DenialReason = "unexpected" }).CapabilityVersion);
        Assert.Null(EllaiObservedToolResult.From(request, result with { Truncated = true }).CapabilityVersion);
        Assert.Null(EllaiObservedToolResult.From(new EllaiToolRequest(EllaiToolNames.Capabilities,
            new Dictionary<string, string> { ["feature"] = "cases" }), result).CapabilityVersion);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var unmarked = EllaiToolTrace.SerializeFlattened([observation with { CapabilityVersion = null }], false, []);
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with { CapabilityVersion = "capability-descriptor/2" }], false, []));
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with { ToolName = EllaiToolNames.Now }], false, []));
        var turns = new[] { Turn("known", 1, "capability?", "known capability answer", trace),
            Turn("unknown", 2, "other?", "unknown capability answer", unmarked),
            Turn("current", 3, "what did you say?", null, null) };
        var replay = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
        Assert.Contains(replay.Messages, message => message.Content == "known capability answer");
        Assert.DoesNotContain(replay.Messages, message => message.Content == "unknown capability answer");
        Assert.Contains(replay.InheritedObservations, item => item.CapabilityVersion == EllaiObservedToolResult.CurrentCapabilityVersion);
    }

    [Fact]
    public async Task ServerClockHistorySurvivesReplayButUnknownClockTracesDoNot()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo);
        var request = new EllaiToolRequest(EllaiToolNames.Now, new Dictionary<string, string>());
        var result = await broker.InvokeAsync(context, request, CancellationToken.None);
        Assert.True(result.Allowed);
        Assert.DoesNotContain("HistoryReplayVersion", System.Text.Json.JsonSerializer.Serialize(result));
        var observation = EllaiObservedToolResult.From(request, result);
        Assert.Equal(EllaiObservedToolResult.CurrentClockVersion, observation.ClockVersion);

        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        var unknown = EllaiToolTrace.SerializeFlattened([observation with { ClockVersion = null }], false, []);
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with { ToolName = EllaiToolNames.Topic }], false, []));
        Assert.Null(EllaiToolTrace.SerializeFlattened([observation with { ClockVersion = "server-clock/2" }], false, []));
        var turns = new[] { Turn("known", 1, "what time?", "the clock showed a time", trace),
            Turn("unknown", 2, "another clock?", "unknown clock answer", unknown),
            Turn("current", 3, "what was it?", null, null) };
        var replay = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);

        Assert.Contains(replay.Messages, message => message.Content == "the clock showed a time");
        Assert.DoesNotContain(replay.Messages, message => message.Content == "unknown clock answer");
        Assert.Contains(replay.InheritedObservations, item => item.ClockVersion == EllaiObservedToolResult.CurrentClockVersion);
    }

    [Theory]
    [InlineData("allowed", true)]
    [InlineData("revoked", false)]
    [InlineData("changed", false)]
    [InlineData("outage", false)]
    public async Task RechecksCaseVersionsAndInheritedTurns(string state, bool replay)
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var trace = EllaiToolTrace.Serialize([new(EllaiToolNames.Topic, true, "Ok", [evidence], false, CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1))], false);
        var inherited = EllaiToolTrace.Serialize([], true, ["one"]);
        var turns = new[] { Turn("one", 1, "question", $"case {id:D}", trace),
            Turn("two", 2, "follow up", "derived response", inherited),
            Turn("three", 3, "current", null, null) };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "three", Actor(), new Queue(state, id), default);
        Assert.Equal(replay, result.Messages.Any(item => item.Content.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(replay, result.Messages.Any(item => item.Content == "derived response"));
        Assert.Equal(!replay, result.Messages.Any(item => item.Content.Contains("could not be verified", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task VersionOneInheritedHistoryIsUnknownEvenWhenSomeEarlierTurnsRemain()
    {
        var clean = EllaiToolTrace.Serialize([], false);
        var unknownDependencies = EllaiToolTrace.Serialize([], true);
        var turns = new[] { Turn("remaining", 2, "question", "safe answer", clean),
            Turn("inherited", 3, "next", "possibly private answer", unknownDependencies),
            Turn("current", 4, "current", null, null) };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
        Assert.Contains(result.Messages, item => item.Content == "safe answer");
        Assert.DoesNotContain(result.Messages, item => item.Content == "possibly private answer");
    }

    [Fact]
    public async Task CaseHistoryDoesNotReplayForAnotherActor()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var trace = EllaiToolTrace.Serialize([new(EllaiToolNames.Topic, true, "Ok", [evidence], false, CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1))], false);
        var turns = new[] { Turn("one", 1, "question", $"case {id:D}", trace), Turn("current", 2, "current", null, null) };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor("other"), new Queue("allowed", id), default);
        Assert.DoesNotContain(result.Messages, item => item.Content.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingOrForwardHistoryDependenciesCannotBeReplayed()
    {
        var missing = EllaiToolTrace.Serialize([], true, ["missing"]);
        var forward = EllaiToolTrace.Serialize([], true, ["later"]);
        var clean = EllaiToolTrace.Serialize([], false, []);
        var turns = new[] { Turn("one", 1, "q", "missing dependent", missing),
            Turn("two", 2, "q", "forward dependent", forward),
            Turn("later", 3, "q", "safe answer", clean), Turn("current", 4, "q", null, null) };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
        Assert.DoesNotContain(result.Messages, item => item.Content is "missing dependent" or "forward dependent");
        Assert.Contains(result.Messages, item => item.Content == "safe answer");
    }

    [Fact]
    public async Task LongConversationLosesInheritedContextWhenTheOldestDependencyFallsOutsideTheStoreWindow()
    {
        var turns = new List<EllaiConversationTurn>();
        for (var number = 1; number <= 101; number++)
        {
            var trace = EllaiToolTrace.Serialize([], number > 1,
                number == 1 ? [] : [$"turn-{number - 1}"]);
            turns.Add(Turn($"turn-{number}", number, $"question {number}", $"answer {number}", trace));
        }
        turns.Add(Turn("current", 102, "next", null, null));

        // Model the PostgreSQL store's most recent 100 rows, including the current turn.
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns.TakeLast(100).ToArray(),
            "current", Actor(), null, default);

        Assert.Empty(result.RetainedTurnIds);
        Assert.Contains(result.Messages, message => message.Content.Contains("could not be verified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlattenedProvenancePreservesLongHistoryAndStillRechecksOriginalCaseAccess()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var observation = new EllaiObservedToolResult(EllaiToolNames.Topic, true, "Ok", [evidence], false, CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        var turns = new List<EllaiConversationTurn>
        {
            Turn("turn-1", 1, "question 1", $"case {id:D}", EllaiToolTrace.SerializeFlattened([observation], false, []))
        };
        for (var number = 2; number <= 110; number++)
        {
            var current = Turn($"turn-{number}", number, $"question {number}", null, null);
            var window = turns.Append(current).TakeLast(100).ToArray();
            var result = await EllaiAuthorizedConversationContext.BuildAsync(window, current.ClientUtteranceId,
                Actor(), new Queue("allowed", id), default);
            Assert.Contains(result.Messages, message => message.Content == $"answer {number - 1}" ||
                number == 2 && message.Content.Contains(id.ToString("D"), StringComparison.Ordinal));
            var trace = EllaiToolTrace.SerializeFlattened([], true, result.InheritedObservations);
            Assert.NotNull(trace);
            turns.Add(Turn(current.ClientUtteranceId, number, current.UserText!, $"answer {number}", trace));
        }
        var final = Turn("current", 111, "next", null, null);
        var lastWindow = turns.Append(final).TakeLast(100).ToArray();
        var revoked = await EllaiAuthorizedConversationContext.BuildAsync(lastWindow, "current", Actor(), new Queue("revoked", id), default);
        Assert.Empty(revoked.RetainedTurnIds);
        Assert.DoesNotContain(revoked.Messages, message => message.Content == "answer 110");
    }

    [Fact]
    public async Task VersionTwoDependenciesAreCarriedIntoVersionThreeAndRecheckedAfterOldRowsDisappear()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var original = EllaiToolTrace.Serialize([new(EllaiToolNames.Topic, true, "Ok", [evidence], false, CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1))], false, []);
        var dependent = EllaiToolTrace.Serialize([], true, ["original"]);
        var oldTurns = new[] { Turn("original", 1, "question", $"case {id:D}", original),
            Turn("dependent", 2, "follow up", "derived answer", dependent), Turn("current", 3, "next", null, null) };
        var authorized = await EllaiAuthorizedConversationContext.BuildAsync(oldTurns, "current", Actor(), new Queue("allowed", id), default);
        var flattened = EllaiToolTrace.SerializeFlattened([], true, authorized.InheritedObservations);
        Assert.NotNull(flattened);

        // Both original version 2 rows are now absent from the bounded read.
        var restored = new[] { Turn("current", 3, "next", "new derived answer", flattened),
            Turn("next", 4, "continue", null, null) };
        var stillAllowed = await EllaiAuthorizedConversationContext.BuildAsync(restored, "next", Actor(), new Queue("allowed", id), default);
        Assert.Contains(stillAllowed.Messages, item => item.Content == "new derived answer");
        var revoked = await EllaiAuthorizedConversationContext.BuildAsync(restored, "next", Actor(), new Queue("revoked", id), default);
        Assert.DoesNotContain(revoked.Messages, item => item.Content == "new derived answer");
    }

    [Fact]
    public async Task FrameworkRunnerStopsBeforeModelWhenAccessChangesAfterHistorySelection()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var trace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.Topic, true, "Ok", [evidence], false, CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1))], false, []);
        var turns = new[] { Turn("prior", 1, "question", $"case {id:D}", trace), Turn("current", 2, "next", null, null) };
        var queue = new RevokingQueue(id);
        using var chat = new CountingChatClient();
        var consent = new EllaiConsentService();
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var prompts = new EllaiPromptBuilder("Synthetic policy", "fixture");
        IEllaiReasoningExecutor loop = new EllaiFrameworkExecutor(chat, prompts, broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var work = new EllaiUtteranceWork("session", "scope", "next", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", Actor(), true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose, ClientUtteranceId: "current");
        var outcome = await EllaiUtteranceRunner.RunOneAsync(work,
            new(loop, null, null, consent, Outbox: new ConversationOutbox(turns), CaseQueue: queue), 256, default);
        Assert.False(outcome.Completed);
        Assert.Equal(0, chat.Calls);
        Assert.True(queue.Calls >= 2);
    }

    [Fact]
    public async Task CaseRecallHistoryExpiresAtTheOriginalSnapshotDeadline()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        async Task<bool> Replayed(DateTimeOffset? expires)
        {
            var observation = new EllaiObservedToolResult(EllaiToolNames.Topic, true, "Ok", [evidence], false,
                CaseSnapshotExpiresAtUtc: expires);
            var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
            var turns = new[] { Turn("prior", 1, "question", "retained case", trace), Turn("current", 2, "next", null, null) };
            var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), new Queue("allowed", id), default);
            return result.Messages.Any(item => item.Content == "retained case");
        }
        Assert.True(await Replayed(DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.False(await Replayed(DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.False(await Replayed(null));
    }

    [Fact]
    public async Task CaseListHistoryRequiresUnchangedAuthorizedQueryResults()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var query = new EllaiCaseListQuery(10, "ZW", "Reported", "latest-reported");
        var trace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.State, true, "Ok", [evidence], false, CaseListQuery: query, QueueProjectionVersion: EllaiObservedToolResult.CurrentQueueProjectionVersion)], false, []);
        var turns = new[] { Turn("prior", 1, "list cases", "One case was listed.", trace), Turn("current", 2, "what now?", null, null) };
        var queue = new ListQueue(id);
        var unchanged = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.Contains(unchanged.Messages, item => item.Content == "One case was listed.");
        Assert.Equal(new CaseQueueFilter("ZW", "Reported", true), queue.LastFilter);
        queue.IncludeNewCase = true;
        var changed = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.DoesNotContain(changed.Messages, item => item.Content == "One case was listed.");
        var unscoped = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.State, true, "Ok", [evidence], false)], false, []);
        var old = await EllaiAuthorizedConversationContext.BuildAsync([turns[0] with { ToolTraceJson = unscoped }, turns[1]], "current", Actor(), queue, default);
        Assert.DoesNotContain(old.Messages, item => item.Content == "One case was listed.");
    }

    [Fact]
    public async Task CompleteCaseListAtTheRequestedLimitRemainsAvailableInHistory()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var query = new EllaiCaseListQuery(1, "ZW", "Reported", "latest-reported");
        var trace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.State, true, "Ok", [evidence], false, CaseListQuery: query, QueueProjectionVersion: EllaiObservedToolResult.CurrentQueueProjectionVersion)], false, []);
        var turns = new[] { Turn("prior", 1, "list cases", "One case was listed.", trace), Turn("current", 2, "what now?", null, null) };
        var queue = new ListQueue(id);

        var unchanged = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.Contains(unchanged.Messages, item => item.Content == "One case was listed.");
        queue.IncludeNewCase = true;
        var changed = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.DoesNotContain(changed.Messages, item => item.Content == "One case was listed.");
    }

    [Fact]
    public async Task CappedCaseListReplaysOnlyWhenRowsAndContinuationStatusMatch()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var query = new EllaiCaseListQuery(1, "ZW", "Reported", "latest-reported");
        var trace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.State, true, "Ok", [evidence], true, CaseListQuery: query, QueueProjectionVersion: EllaiObservedToolResult.CurrentQueueProjectionVersion)], false, []);
        var turns = new[] { Turn("prior", 1, "latest case", "First case.", trace), Turn("current", 2, "next", null, null) };
        var queue = new ListQueue(id) { NextCursor = "next" };
        var unchanged = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.Contains(unchanged.Messages, item => item.Content == "First case.");
        queue.NextCursor = null;
        var complete = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.DoesNotContain(complete.Messages, item => item.Content == "First case.");
    }

    [Fact]
    public async Task LegacyCaseOutputsAndClinicalFieldEvidenceCannotBeReplayedToTheModel()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var legacyList = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.State, true, "Ok", [evidence], false,
            CaseListQuery: new EllaiCaseListQuery(10, "ZW", "Reported", "latest-reported"))], false, []);
        var clinical = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.Evidence, true, "Ok",
            [evidence with { FieldPath = "classification" }], false, CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5))], false, []);
        foreach (var trace in new[] { legacyList, clinical })
        {
            var turns = new[] { Turn("prior", 1, "question", "sensitive old answer", trace), Turn("current", 2, "next", null, null) };
            var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), new ListQueue(id), default);
            Assert.DoesNotContain(result.Messages, item => item.Content == "sensitive old answer");
        }
    }

    [Fact]
    public async Task DuplicateCaseEvidenceIsReadOncePerPassAndRecheckedOnTheNextPass()
    {
        var id = Guid.NewGuid();
        var evidence = new EvidenceRef("CaseQueue", id.ToString("D"), "3", null, id.ToString("D"));
        var trace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.Topic, true, "Ok", [evidence], false, CaseSnapshotExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1))], false, []);
        var turns = Enumerable.Range(1, 6).Select(index => Turn($"turn-{index}", index, "question", "answer", trace))
            .Append(Turn("current", 7, "next", null, null)).ToArray();
        var queue = new SwitchableQueue(id);
        var first = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.Equal(6, first.RetainedTurnIds.Count);
        Assert.Equal(1, queue.Calls);
        queue.Revoked = true;
        var second = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), queue, default);
        Assert.Empty(second.RetainedTurnIds);
        Assert.Equal(2, queue.Calls);
    }

    [Fact]
    public async Task DuplicateImportEvidenceIsReadOncePerPassAndRecheckedOnTheNextPass()
    {
        var id = Guid.NewGuid();
        var updated = DateTimeOffset.UtcNow;
        var evidence = new EvidenceRef("ImportJob", id.ToString("D"), updated.ToString("O"), "status", id.ToString("D"));
        var trace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.ImportStatus, true, "Ok", [evidence], false)], false, []);
        var turns = Enumerable.Range(1, 6).Select(index => Turn($"turn-{index}", index, "question", "answer", trace))
            .Append(Turn("current", 7, "next", null, null)).ToArray();
        var imports = new CountingImportStore(id, updated);
        var first = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default, imports: imports);
        Assert.Equal(6, first.RetainedTurnIds.Count);
        Assert.Equal(1, imports.Calls);
        imports.Revoked = true;
        var second = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default, imports: imports);
        Assert.Empty(second.RetainedTurnIds);
        Assert.Equal(2, imports.Calls);
    }

    [Fact]
    public async Task CallerCancellationStopsHistoryAuthorization()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var trace = EllaiToolTrace.Serialize([], false, []);
        var turns = new[] { Turn("one", 1, "q", "answer", trace), Turn("current", 2, "q", null, null) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EllaiAuthorizedConversationContext.BuildAsync(
            turns, "current", Actor(), null, cancellation.Token));
    }

    [Fact]
    public async Task ImportHistoryRequiresCurrentOwnerScopeAndStableTerminalVersion()
    {
        var id = Guid.NewGuid();
        var updated = DateTimeOffset.UtcNow;
        var evidence = new EvidenceRef("ImportJob", id.ToString("D"), updated.ToString("O"), "status", id.ToString("D"));
        var trace = EllaiToolTrace.Serialize([new(EllaiToolNames.ImportStatus, true, "Ok", [evidence], false)], false);
        var turns = new[] { Turn("import", 1, "question", "import answer", trace), Turn("current", 2, "next", null, null) };
        async Task<bool> Replayed(string state, DateTimeOffset version, string actor = "owner")
        {
            var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(actor), null, default,
                imports: new ImportStore(id, state, version));
            return result.Messages.Any(item => item.Content == "import answer");
        }
        Assert.True(await Replayed("Completed", updated));
        Assert.False(await Replayed("Completed", updated.AddSeconds(1)));
        Assert.False(await Replayed("Completed", updated, "other"));
        Assert.False(await Replayed("Running", updated));
        Assert.False(await Replayed("Paused", updated));
        Assert.False(await Replayed("revoked", updated));
    }

    [Fact]
    public async Task SuccessfulFormatValidationCanRemainInMultiTurnHistory()
    {
        var trace = EllaiToolTrace.Serialize([new(EllaiToolNames.ValidateObservations, true, "Ok", [], false)], false);
        var turns = new[] { Turn("validation", 1, "question", "format is valid", trace),
            Turn("current", 2, "next", null, null) };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
        Assert.Contains(result.Messages, item => item.Content == "format is valid");
    }

    [Fact]
    public async Task DocumentationHistoryRequiresTheSameApprovedRevision()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ellai-history-doc-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(path, "# Reference\n\n**Status: Approved.**\n\n## Capture\n\nOriginal instructions.\n");
            var docs = new EllaiTechnicalDocs(path);
            var section = docs.Get("capture")!;
            Assert.NotNull(section);
            var reference = new EvidenceRef("Documentation", section.SectionId, section.Revision, null, section.SectionId);
            var trace = EllaiToolTrace.Serialize([new(EllaiToolNames.DocsGet, true, "Ok", [reference], false)], false);
            var turns = new[] { Turn("docs", 1, "question", "document answer", trace), Turn("current", 2, "next", null, null) };
            var original = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default, docs);
            Assert.Contains(original.Messages, item => item.Content == "document answer");
            await File.AppendAllTextAsync(path, "\nChanged instructions.\n");
            var changed = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default, docs);
            Assert.DoesNotContain(changed.Messages, item => item.Content == "document answer");
            var missing = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
            Assert.DoesNotContain(missing.Messages, item => item.Content == "document answer");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task IngestionSchemaHistoryRequiresCurrentEvidence()
    {
        var evidence = new EvidenceRef("ProductFact", "ingestion-schema", "1", "capture", "ingestion-schema/1");
        async Task<bool> Replayed(EvidenceRef reference)
        {
            var trace = EllaiToolTrace.Serialize([new(EllaiToolNames.IngestionSchema, true, "Ok", [reference], false)], false);
            var turns = new[] { Turn("schema", 1, "question", "schema answer", trace), Turn("current", 2, "next", null, null) };
            var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
            return result.Messages.Any(item => item.Content == "schema answer");
        }
        Assert.True(await Replayed(evidence));
        Assert.False(await Replayed(evidence with { Version = "old-version" }));
    }

    [Fact]
    public async Task ProductFactHistoryRequiresCurrentCatalogRevision()
    {
        var fact = new EllaiProductFactCatalog().Find(null)[0];
        var evidence = new EvidenceRef("ProductFact", fact.FactId, fact.Revision, fact.SourceId, fact.FactId);
        async Task<bool> Replayed(EvidenceRef reference)
        {
            var trace = EllaiToolTrace.Serialize([new(EllaiToolNames.ProductFacts, true, "Ok", [reference], false)], false);
            var turns = new[] { Turn("fact", 1, "question", "product answer", trace), Turn("current", 2, "next", null, null) };
            var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
            return result.Messages.Any(item => item.Content == "product answer");
        }
        Assert.True(await Replayed(evidence));
        Assert.False(await Replayed(evidence with { Version = "old-version" }));
    }

    [Fact]
    public async Task OmittedLegacyTurnDoesNotTaintLaterTurnThatDidNotReceiveIt()
    {
        var clean = EllaiToolTrace.Serialize([], historyIncluded: true, historyTurnIds: []);
        var turns = new[] { Turn("legacy", 1, "old question", "private old answer", null),
            Turn("clean", 2, "new question", "new answer", clean),
            Turn("current", 3, "current", null, null) };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
        Assert.Contains(result.Messages, item => item.Content == "new answer");
        Assert.DoesNotContain(result.Messages, item => item.Content == "private old answer");
        Assert.Equal(["clean"], result.RetainedTurnIds);
    }

    [Fact]
    public async Task UnknownAndUnsupportedToolTracesCannotBeReplayed()
    {
        var unsafeTrace = EllaiToolTrace.Serialize([new(EllaiToolNames.Imports, true, "Ok", [], false)], false);
        var turns = new[] { Turn("legacy", 1, "legacy user", "legacy answer", null),
            Turn("private", 2, "private user", "private answer", unsafeTrace),
            Turn("current", 3, "current", null, null) };
        var result = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor(), null, default);
        Assert.DoesNotContain(result.Messages, item => item.Content.Contains("legacy answer", StringComparison.Ordinal) ||
            item.Content.Contains("private answer", StringComparison.Ordinal));
        Assert.Contains(result.Messages, item => item.Content.Contains("could not be verified", StringComparison.Ordinal));
    }

    private static EllaiConversationTurn Turn(string id, long sequence, string user, string? answer, string? trace) =>
        new("session", id, sequence, user, answer, answer is null ? "Queued" : "Completed", 1,
            answer is null ? null : DateTimeOffset.UtcNow) { ToolTraceJson = trace };

    private static ActorContext Actor(string actorId = "owner") => ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, actorId), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));

    private sealed class Queue(string state, Guid id) : ICaseQueueQuery
    {
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct)
        {
            if (state == "outage") throw new InvalidOperationException("fixture outage");
            IReadOnlyList<CaseQueueItem> items = state == "revoked" || actor.ActorId != "owner" ? [] :
                [new(id, "Reported", "SZ", actor.ActorId, state == "changed" ? 4 : 3, DateTimeOffset.UtcNow)];
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, items));
        }
    }

    private sealed class RevokingQueue(Guid id) : ICaseQueueQuery
    {
        public int Calls { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct)
        {
            Calls++;
            IReadOnlyList<CaseQueueItem> items = Calls == 1
                ? [new(id, "Reported", "SZ", actor.ActorId, 3, DateTimeOffset.UtcNow)] : [];
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, items));
        }
    }

    private sealed class ListQueue(Guid id) : ICaseQueueQuery
    {
        public bool IncludeNewCase { get; set; }
        public string? NextCursor { get; set; }
        public CaseQueueFilter? LastFilter { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            var items = new List<CaseQueueItem> { new(id, "Reported", "ZW", actor.ActorId, 3, DateTimeOffset.UtcNow) };
            if (IncludeNewCase) items.Add(new(Guid.NewGuid(), "Reported", "ZW", actor.ActorId, 1, DateTimeOffset.UtcNow));
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, items, NextCursor));
        }
    }

    private sealed class SwitchableQueue(Guid id) : ICaseQueueQuery
    {
        public bool Revoked { get; set; }
        public int Calls { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct)
        {
            Calls++;
            IReadOnlyList<CaseQueueItem> items = Revoked ? [] : [new(id, "Reported", "SZ", actor.ActorId, 3, DateTimeOffset.UtcNow)];
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, items));
        }
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The model should not receive revoked history.");
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The model should not receive revoked history.");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ConversationOutbox(IReadOnlyList<EllaiConversationTurn> turns) : IEllaiUtteranceOutboxStore
    {
        public Task<IReadOnlyList<EllaiConversationTurn>> ReadConversationAsync(string sessionId, string ownerActorId, CancellationToken ct) => Task.FromResult(turns);
        public Task<EllaiConversationTurn?> ReadLatestGuidanceTurnAsync(string sessionId, string ownerActorId, CancellationToken ct) => throw new NotSupportedException();
        public Task<EllaiUtteranceOutboxItem?> ClaimNextAsync(DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct) => throw new NotSupportedException();
        public Task<EllaiUtteranceResolution?> CompleteAsync(string sessionId, string clientUtteranceId, int attemptCount, DateTimeOffset completedAtUtc, CancellationToken ct, EllaiUtteranceResolution? resolution = null) => throw new NotSupportedException();
        public Task FailAsync(string sessionId, string clientUtteranceId, int attemptCount, DateTimeOffset retryAtUtc, bool terminal, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ImportStore(Guid id, string state, DateTimeOffset updated) : ICaseImportStore
    {
        public Task<CaseImportSummary?> GetAsync(ActorContext actor, Guid importId, CancellationToken ct)
        {
            if (state == "revoked") throw new CaseImportException("not-authorized");
            return Task.FromResult<CaseImportSummary?>(actor.ActorId == "owner" && importId == id
                ? new(id, state, 2, 1, 0, 0, 0, null, updated) : null);
        }
        public Task<CaseImportLease> OpenAsync(ActorContext actor, Guid importId, CaseImportContext context, long startRow, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> BeginRowAsync(CaseImportLease lease, long row, Guid? commandId, byte[] sourceHash, byte[] sourceBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteRowAsync(CaseImportLease lease, CaseImportRowOutcome result, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseAsync(CaseImportLease lease, string state, string? errorCode, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CountingImportStore(Guid id, DateTimeOffset updated) : ICaseImportStore
    {
        public bool Revoked { get; set; }
        public int Calls { get; private set; }
        public Task<CaseImportSummary?> GetAsync(ActorContext actor, Guid importId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<CaseImportSummary?>(!Revoked && importId == id
                ? new(id, "Completed", 2, 1, 0, 0, 0, null, updated) : null);
        }
        public Task<CaseImportLease> OpenAsync(ActorContext actor, Guid importId, CaseImportContext context, long startRow, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> BeginRowAsync(CaseImportLease lease, long row, Guid? commandId, byte[] sourceHash, byte[] sourceBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteRowAsync(CaseImportLease lease, CaseImportRowOutcome result, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseAsync(CaseImportLease lease, string state, string? errorCode, CancellationToken ct) => throw new NotSupportedException();
    }
}
