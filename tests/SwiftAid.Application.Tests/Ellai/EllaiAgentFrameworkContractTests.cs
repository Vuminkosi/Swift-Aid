using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;
using EvidenceRef = SwiftAid.Contracts.Ellai.EvidenceRef;
using EllaiDecision = SwiftAid.Contracts.Ellai.EllaiDecision;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiAgentFrameworkContractTests
{
    private const string ValidDecision = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.",
         "spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
         "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

    [Fact]
    public async Task CountAnswerAndStreamUseOnlyAuthorizedObservationEvenWhenModelInventsValue()
    {
        using var client = new CountStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        var reference = new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof");
        const string authorized = "2 managed case(s) in the authorized window.";
        var chunks = new List<string>();
        var result = await agent.RunAsync(context, "Test policy", [new ChatMessage(ChatRole.User, "Count")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [reference], authorized, 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });
        Assert.NotNull(result);
        Assert.Equal(authorized, result.DisplayText);
        Assert.Equal(authorized, result.SpokenText);
        Assert.Equal(authorized, string.Concat(chunks));
        Assert.Contains(reference, Assert.Single(result.Claims).Evidence);
    }

    [Fact]
    public async Task CountRevalidationTimeoutWithholdsAnswerAndKeepsToolAccounting()
    {
        using var client = new CountStreamingClient();
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(),
            new InMemoryEllaiUsageStore(), TimeSpan.FromMilliseconds(100));
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        var calls = 0;
        var chunks = new List<string>();
        var events = new List<EllaiActivityEvent>();
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "Count", new EllaiRoleOverlay("ask", "help"),
            "count", 100, "cycle", 1), default, onActivity: events.Add,
            onTextDelta: (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; },
            invokeToolAsync: async (_, request, token) =>
            {
                if (++calls == 2) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof")],
                    "2 managed case(s) in scope.", 1, false);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outcome.Completed);
        Assert.Null(outcome.Decision);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, outcome.FailureCode);
        Assert.Equal(2, calls);
        Assert.Equal(1, outcome.ToolCallsUsed);
        Assert.Empty(chunks);
        Assert.Single(events, item => item.Kind == EllaiActivityKind.ToolStarted);
    }

    [Fact]
    public async Task CancellingCountRevalidationPropagatesWithoutPublishing()
    {
        using var client = new CountStreamingClient();
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new List<string>();
        var calls = 0;
        var run = executor.RunAsync(new EllaiLoopRequest(context, "Count", new EllaiRoleOverlay("ask", "help"),
            "count", 100, "cycle", 1), cancellation.Token,
            onTextDelta: (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; },
            invokeToolAsync: async (_, request, token) =>
            {
                if (++calls == 2)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof")],
                    "2 managed case(s) in scope.", 1, false);
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(chunks);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CountRevalidationDoesNotConsumeModelToolBudgetOrDuplicateProgress()
    {
        using var client = new CountStreamingClient();
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        var calls = 0;
        var events = new List<EllaiActivityEvent>();
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "Count", new EllaiRoleOverlay("ask", "help"),
            "count", 100, "cycle", 1), default, onActivity: events.Add,
            onTextDelta: (_, _) => ValueTask.CompletedTask, invokeToolAsync: (_, request, _) =>
            {
                calls++;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof")],
                    "2 managed case(s) in scope.", 1, false));
            });
        Assert.True(outcome.Completed);
        Assert.Equal(2, calls);
        Assert.Equal(1, outcome.ToolCallsUsed);
        Assert.Single(events, item => item.Kind == EllaiActivityKind.ToolStarted);
        Assert.Single(events, item => item.Kind == EllaiActivityKind.ToolObservationAccepted);
    }

    [Fact]
    public async Task CountChangedBeforePublicationIsNotPublished()
    {
        using var client = new CountStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        var calls = 0;
        var chunks = new List<string>();
        var result = await agent.RunAsync(context, "Test policy", [new ChatMessage(ChatRole.User, "Count")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                [new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof")],
                ++calls == 1 ? "2 managed case(s) in scope." : "0 managed case(s) in scope.", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });
        Assert.Null(result);
        Assert.Empty(chunks);
        Assert.Equal(2, calls);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, agent.LastFailureCode);
    }

    [Fact]
    public async Task CountDoesNotPublishWhenAuthorizationExpiresDuringToolCall()
    {
        using var client = new CountStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        var current = true;
        var chunks = new List<string>();
        var failure = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context, "Test policy", [new ChatMessage(ChatRole.User, "Count")], 100,
            _ => Task.FromResult(current), (_, request, _) =>
            {
                current = false;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof")], "2 cases.", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; }));
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, failure.Code);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task CountDoesNotReturnSuccessWhenExecutionBecomesStaleBeforePublication()
    {
        using var client = new CountStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        var revalidated = false;
        var postRevalidationChecks = 0;
        var chunks = new List<string>();
        var reference = new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof");
        Task<EllaiToolResult> Read(EllaiToolRequest request) => Task.FromResult(new EllaiToolResult(request.ToolName,
            true, null, [reference], "2 managed case(s) in scope.", 1, false));
        var result = await agent.RunAsync(context, "Test policy", [new ChatMessage(ChatRole.User, "Count")], 100,
            _ => Task.FromResult(!revalidated || ++postRevalidationChecks == 1),
            (_, request, _) => Read(request),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; },
            (_, request, _) => { revalidated = true; return Read(request); });
        Assert.Null(result);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, agent.LastFailureCode);
        Assert.Empty(chunks);
        Assert.Equal(2, postRevalidationChecks);
    }

    [Fact]
    public async Task ExecutionThatBecomesStaleDuringPublicationDoesNotReturnSuccess()
    {
        using var client = new StreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        var current = true;
        var chunks = new List<string>();
        var result = await agent.RunAsync(context, "Test policy", [new ChatMessage(ChatRole.User, "Hello")], 100,
            _ => Task.FromResult(current), (_, _, _) => throw new InvalidOperationException("No tool call expected."),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); current = false; return ValueTask.CompletedTask; });
        Assert.Null(result);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, agent.LastFailureCode);
        Assert.Equal("Done.", string.Concat(chunks));
    }

    [Fact]
    public async Task CountMixedWithAnotherToolDoesNotPublishUnverifiedAnswer()
    {
        using var client = new CountStreamingClient(mixed: true);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount, EllaiToolNames.Now });
        var reference = new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof");
        var chunks = new List<string>();
        var result = await agent.RunAsync(context, "Test policy", [new ChatMessage(ChatRole.User, "Count")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [reference], "2 managed case records.", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });
        Assert.Null(result);
        Assert.Empty(chunks);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, agent.LastFailureCode);
    }

    private sealed class CountStreamingClient(bool mixed = false) : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (++_calls == 1)
            {
                var calls = new List<AIContent> { new FunctionCallContent("count", EllaiToolNames.OwnedCaseCount,
                    new Dictionary<string, object?> { ["window_days"] = "30" }) };
                if (mixed) calls.Add(new FunctionCallContent("now", EllaiToolNames.Now, new Dictionary<string, object?>()));
                yield return new ChatResponseUpdate(ChatRole.Assistant, calls);
            }
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision.Replace("Done.", "There are 999 cases.", StringComparison.Ordinal));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FrameworkReasonsAcrossDependentToolCallsAndRejectsUnobservedEvidence(bool citeRealEvidence)
    {
        using var client = new DependentToolClient(citeRealEvidence);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now, EllaiToolNames.DocsGet });
        var calls = new List<string>();
        var evidence = new EvidenceRef("Document", "safe", "v1", null, "proof-1");
        var decision = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 300,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                calls.Add(request.ToolName);
                return Task.FromResult(request.ToolName == EllaiToolNames.Now
                    ? new EllaiToolResult(request.ToolName, true, null, [], "Use section safe", 1, false)
                    : new EllaiToolResult(request.ToolName, true, null, [evidence], "Verified observation", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None);

        Assert.Equal(new[] { EllaiToolNames.Now, EllaiToolNames.DocsGet }, calls);
        Assert.Equal(citeRealEvidence ? 3 : 5, client.Calls);
        Assert.Contains("Use section safe", client.FirstObservation);
        Assert.Contains("Verified observation", client.SecondObservation);
        Assert.Equal(citeRealEvidence, decision is not null);
    }

    [Theory]
    [InlineData(1, true, 2)]
    [InlineData(3, false, 3)]
    public async Task FrameworkRepairsInvalidFinalDecisionsWithoutTools(int invalidReplies, bool succeeds, int expectedCalls)
    {
        using var client = new RepairClient(invalidReplies);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var store = new InMemoryEllaiUsageStore();
        var decision = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), store), CancellationToken.None);
        Assert.Equal(succeeds, decision is not null);
        Assert.Equal(expectedCalls, client.Calls);
        Assert.All(client.RepairToolCounts, count => Assert.Equal(0, count));
        Assert.Equal(expectedCalls, (await store.TotalsAsync("session", CancellationToken.None)).Calls);
    }

    [Fact]
    public async Task RepairedDecisionDoesNotAppendToRejectedStreamedDraft()
    {
        using var client = new StreamingRepairClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal));
        var fragments = new List<string>();
        var decision = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
            (text, _) => { fragments.Add(text); return ValueTask.CompletedTask; });
        Assert.Equal("Done.", decision?.DisplayText);
        Assert.Equal("Wrong.", string.Concat(fragments));
        Assert.Equal(1, client.Repairs);
    }

    [Fact]
    public async Task ConcurrentFrameworkSessionsDoNotShareConversationContext()
    {
        using var client = new HistoryCaptureClient();
        async Task<EllaiDecision?> Run(string id, string secret)
        {
            var agent = new EllaiFrameworkAgent(client);
            var context = new EllaiToolContext(id, "epoch", "cases", null, 4, true,
                AllowedTools: new HashSet<string>(StringComparer.Ordinal));
            return await agent.RunAsync(context, "Test instructions",
                [new ChatMessage(ChatRole.User, secret), new ChatMessage(ChatRole.Assistant, "prior answer"),
                 new ChatMessage(ChatRole.User, "follow-up")], 100, _ => Task.FromResult(true),
                (_, _, _) => throw new InvalidOperationException(),
                new EllaiFrameworkUsage(id, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None);
        }

        var outcomes = await Task.WhenAll(Run("session-a", "only-a"), Run("session-b", "only-b"));
        Assert.All(outcomes, outcome => Assert.NotNull(outcome));
        var histories = client.Histories.ToArray();
        Assert.Equal(2, histories.Length);
        Assert.Single(histories, history => history.Contains("only-a") && !history.Contains("only-b"));
        Assert.Single(histories, history => history.Contains("only-b") && !history.Contains("only-a"));
    }

    [Fact]
    public async Task FrameworkToolResultAfterDeadlineIsNotAccepted()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(),
            new InMemoryEllaiUsageStore(), TimeSpan.FromMilliseconds(50));
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "time?", new EllaiRoleOverlay("ask", "help"),
            "clock", 100, "cycle", 1), CancellationToken.None, invokeToolAsync: async (_, tool, _) =>
            {
                await Task.Delay(200);
                return new EllaiToolResult(tool.ToolName, true, null, [], "late observation", 1, false);
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.Completed);
        Assert.Null(outcome.Decision);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, outcome.FailureCode);
    }

    [Fact]
    public async Task FrameworkToolDeadlineStopsCooperativeToolAndRejectsFinalAnswer()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(),
            new InMemoryEllaiUsageStore(), TimeSpan.FromMilliseconds(100));
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var stopped = false;
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "time?", new EllaiRoleOverlay("ask", "help"),
            "clock", 100, "cycle", 1), CancellationToken.None, invokeToolAsync: async (_, _, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { stopped = true; }
                throw new InvalidOperationException("Unexpected tool completion.");
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped);
        Assert.False(outcome.Completed);
        Assert.Null(outcome.Decision);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, outcome.FailureCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameworkToolFailuresAreSanitizedAndCannotProduceAnAnswer(bool asynchronous)
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "time?", new EllaiRoleOverlay("ask", "help"),
            "clock", 100, "cycle", 1), CancellationToken.None,
            invokeToolAsync: (_, _, _) => asynchronous
                ? Task.FromException<EllaiToolResult>(new InvalidOperationException("synthetic-tool-secret"))
                : throw new InvalidOperationException("synthetic-tool-secret"));
        Assert.False(outcome.Completed);
        Assert.Null(outcome.Decision);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, outcome.FailureCode);
        Assert.DoesNotContain("synthetic-tool-secret", outcome.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-tool-secret", client.ToolResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingFrameworkRunCancelsInFlightToolBeforeFurtherModelCalls()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = executor.RunAsync(new EllaiLoopRequest(context, "time?", new EllaiRoleOverlay("ask", "help"),
            "clock", 100, "cycle", 1), cancellation.Token, invokeToolAsync: async (_, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { stopped.TrySetResult(); }
                throw new InvalidOperationException("Unexpected tool completion.");
            });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task FrameworkExecutorReportsBrokerDenialWithoutAcceptingAnswer()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "time?", new EllaiRoleOverlay("ask", "help"),
            "clock", 100, "cycle", 1), CancellationToken.None);
        Assert.False(outcome.Completed);
        Assert.Equal(EllaiToolReasonCodes.ConsentMissing, outcome.FailureCode);
        Assert.Null(outcome.Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderErrorsAreSanitizedAtFrameworkBoundary(bool streaming)
    {
        using var client = new FailingClient();
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal));
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => executor.RunAsync(new EllaiLoopRequest(context, "hello", new EllaiRoleOverlay("ask", "help"),
            "chat", 100, "cycle", 1), CancellationToken.None,
            onTextDelta: streaming ? (_, _) => ValueTask.CompletedTask : null));
        Assert.Equal("ProviderUnavailable", error.Code);
        Assert.DoesNotContain("fixture-secret", error.ToString());
    }

    [Fact]
    public async Task FrameworkSessionRestoresOnlyProvidedConversationHistory()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var messages = new[] { new ChatMessage(ChatRole.User, "earlier question"),
            new ChatMessage(ChatRole.Assistant, "earlier answer"), new ChatMessage(ChatRole.User, "current question") };
        var result = await agent.RunAsync(context, "Test instructions", messages, 100, _ => Task.FromResult(true),
            (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "observation", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None);
        Assert.NotNull(result);
        var relevant = client.FirstMessages.Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant).ToArray();
        Assert.Equal(new[] { "earlier question", "earlier answer", "current question" }, relevant.Select(message => message.Text));
    }

    [Fact]
    public async Task FrameworkExecutorUsesApplicationBoundaryAndPublishesToolActivity()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var consent = new EllaiConsentService();
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        IEllaiReasoningExecutor executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var request = new EllaiLoopRequest(context, "time?", new EllaiRoleOverlay("ask", "help"), "clock", 100, "cycle", 1);
        var events = new List<EllaiActivityEvent>();
        var outcome = await executor.RunAsync(request, CancellationToken.None, onActivity: events.Add,
            invokeToolAsync: (_, tool, _) => Task.FromResult(new EllaiToolResult(tool.ToolName, true, null, [], "observation", 1, false)));
        Assert.True(outcome.Completed);
        Assert.Equal("Done.", outcome.Decision?.DisplayText);
        Assert.Equal(2, outcome.IterationsUsed);
        Assert.Equal(1, outcome.ToolCallsUsed);
        Assert.Contains(events, item => item.Kind == EllaiActivityKind.ToolStarted && item.ToolName == EllaiToolNames.Now);
        Assert.Contains(events, item => item.Kind == EllaiActivityKind.CycleCompleted);
    }

    [Fact]
    public async Task StreamingModelCannotRequestToolOutsideAdmittedCatalogue()
    {
        using var client = new ToolStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal));
        var invoked = false;
        await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context, "Test instructions",
            [new ChatMessage(ChatRole.User, "test")], 100, _ => Task.FromResult(true), (_, _, _) =>
            {
                invoked = true;
                throw new InvalidOperationException();
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()),
            CancellationToken.None, (_, _) => ValueTask.CompletedTask));
        Assert.False(invoked);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task FrameworkStreamingInvokesToolBeforeFinalDecision()
    {
        using var client = new ToolStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var store = new InMemoryEllaiUsageStore();
        var invoked = 0;
        var fragments = new List<string>();
        var decision = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                invoked++;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "observation", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), store), CancellationToken.None,
            (text, _) => { fragments.Add(text); return ValueTask.CompletedTask; });
        Assert.Equal(1, invoked);
        Assert.Equal(2, client.Calls);
        Assert.Equal("Done.", decision?.DisplayText);
        Assert.Equal("Done.", string.Concat(fragments));
        Assert.Equal(2, (await store.TotalsAsync("session", CancellationToken.None)).Calls);
    }

    [Fact]
    public async Task FrameworkStreamingEmitsDecisionTextAndRecordsUsage()
    {
        using var client = new StreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal));
        var store = new InMemoryEllaiUsageStore();
        var fragments = new List<string>();
        var decision = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), store), CancellationToken.None,
            (text, _) => { fragments.Add(text); return ValueTask.CompletedTask; });

        Assert.Equal("Done.", decision?.DisplayText);
        Assert.Equal("Done.", string.Concat(fragments));
        var totals = await store.TotalsAsync("session", CancellationToken.None);
        Assert.Equal(3, totals.PromptTokens);
        Assert.Equal(2, totals.CompletionTokens);
    }

    [Fact]
    public async Task FrameworkExecutionRejectsFinalDecisionAfterToolDenial()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var result = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                false, "denied", [], "", 0, false, EllaiToolReasonCodes.ConsentMissing)), new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task FrameworkExecutionChecksAuthorizationBeforeEveryModelCall()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var checks = 0;
        var usageStore = new InMemoryEllaiUsageStore();
        await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context, "Test instructions",
            [new ChatMessage(ChatRole.User, "test")], 100, _ => Task.FromResult(++checks < 5),
            (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "observation", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), usageStore), CancellationToken.None));
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, (await usageStore.TotalsAsync("session", CancellationToken.None)).Calls);
    }

    [Fact]
    public async Task FrameworkExecutionReturnsValidatedDecisionAfterAllowedTool()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var usageStore = new InMemoryEllaiUsageStore();
        var result = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [], "observation", 1, false)), new EllaiFrameworkUsage("session", new EllaiUsageLedger(), usageStore), CancellationToken.None);
        Assert.Equal("Done.", result?.DisplayText);
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, (await usageStore.TotalsAsync("session", CancellationToken.None)).Calls);
    }
    [Fact]
    public async Task FrameworkAgentInvokesFunctionAndReturnsItsObservation()
    {
        using var client = new ScriptedClient();
        var invoked = 0;
        var tool = AIFunctionFactory.Create((string value) =>
        {
            invoked++;
            return $"observed:{value}";
        }, "lookup");
        AIAgent agent = client.AsAIAgent(name: "EllaiContractFixture", tools: [tool]);
        var session = await agent.CreateSessionAsync();

        var response = await agent.RunAsync("Look up the fixture.", session);

        Assert.Equal(1, invoked);
        Assert.Equal(2, client.Calls);
        Assert.Contains("observed:example", client.ToolResult);
        Assert.Contains("done", response.Text);
    }

    [Fact]
    public async Task FrameworkAgentInvokesRealBrokerForAdmittedPublicFact()
    {
        using var client = new ScriptedClient(EllaiToolNames.ProductFacts, "topic", "guest");
        var context = new EllaiToolContext("session-1", "epoch-1", "cases", null, 4, true,
            ConsentVersion: "consent/0", DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.ProductFacts },
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService());
        var tool = new EllaiFrameworkTool(EllaiToolNames.ProductFacts,
            (request, ct) => broker.InvokeAsync(context, request, ct));
        AIAgent agent = client.AsAIAgent(name: "EllaiBrokerFixture", tools: [tool]);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("What can guests do?", session);

        Assert.Equal(2, client.Calls);
        Assert.Contains("ProductFact", client.ToolResult);
        Assert.Contains("\"allowed\":true", client.ToolResult);
    }

    private sealed class ScriptedClient(string toolName = "lookup", string argumentName = "value", string argumentValue = "example", string finalReply = "done", bool noArguments = false) : IChatClient
    {
        public int Calls { get; private set; }
        public string ToolResult { get; private set; } = string.Empty;
        public IReadOnlyList<ChatMessage> FirstMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1)
            {
                FirstMessages = messages.ToArray();
                Assert.Contains(options!.Tools!, tool => tool.Name == toolName);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", toolName, noArguments ? new Dictionary<string, object?>() : new Dictionary<string, object?> { [argumentName] = argumentValue })])));
            }

            ToolResult = string.Join(" ", messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Select(content => content.Result?.ToString()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, finalReply)));
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

    private sealed class StreamingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision[..60]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision[60..]);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 2 })]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DependentToolClient(bool citeRealEvidence) : IChatClient
    {
        public int Calls { get; private set; }
        public string FirstObservation { get; private set; } = string.Empty;
        public string SecondObservation { get; private set; } = string.Empty;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                .Select(item => item.Result?.ToString() ?? string.Empty).ToArray();
            if (Calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("first", EllaiToolNames.Now, new Dictionary<string, object?>())])));
            if (Calls == 2)
            {
                FirstObservation = Assert.Single(results);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("second", EllaiToolNames.DocsGet, new Dictionary<string, object?> { ["section"] = "safe" })])));
            }
            SecondObservation = results.Last();
            var evidenceId = citeRealEvidence ? "proof-1" : "invented";
            var json = """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Observed.","spokenText":"Observed.",
                "claims":[{"claimId":"c1","text":"Observed.","kind":"Observed","evidence":[{"sourceKind":"Document","resourceId":"safe","version":"v1","fieldPath":null,"evidenceId":"PLACEHOLDER"}]}],
                "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """.Replace("PLACEHOLDER", evidenceId, StringComparison.Ordinal);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HistoryCaptureClient : IChatClient
    {
        public System.Collections.Concurrent.ConcurrentBag<string[]> Histories { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Histories.Add(messages.Select(message => message.Text).ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RepairClient(int invalidReplies) : IChatClient
    {
        public int Calls { get; private set; }
        public List<int> RepairToolCounts { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls > 1) RepairToolCounts.Add(options?.Tools?.Count ?? 0);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Calls <= invalidReplies ? "{}" : ValidDecision)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StreamingRepairClient : IChatClient
    {
        public int Repairs { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Repairs++;
            Assert.Empty(options?.Tools ?? []);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, """{"final_decision":{"displayText":"Wrong."}}""");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FailingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("fixture-secret");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("fixture-secret");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ToolStreamingClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Checking the tool now.");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", EllaiToolNames.Now, new Dictionary<string, object?>())]);
            }
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 2 })]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
