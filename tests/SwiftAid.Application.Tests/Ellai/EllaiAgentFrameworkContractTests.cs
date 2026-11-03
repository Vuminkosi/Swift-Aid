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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringDecisionAuthorityCheckIsNotReportedAsDenial(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new AdmissionCountingClient();
        var checks = 0;
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100,
            _ => { if (++checks == 3) { cancellation.Cancel(); return Task.FromResult(false); } return Task.FromResult(true); },
            (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), cancellation.Token,
            streaming ? (_, _) => ValueTask.CompletedTask : null));
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CancellationDuringToolAuthorityCheckWinsOverItsResult(int cancelAt, bool authorityResult)
    {
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        var calls = 0;
        var guard = new EllaiFrameworkToolGuard(2,
            _ => { if (++checks == cancelAt) { cancellation.Cancel(); return Task.FromResult(authorityResult); } return Task.FromResult(true); },
            (_, request, _) =>
            {
                calls++;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "synthetic", 1, false));
            });
        var context = new EllaiToolContext("session", "epoch", "cases", null, 2, true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.InvokeAsync(context,
            new EllaiToolRequest(EllaiToolNames.Now, new Dictionary<string, string>()), cancellation.Token));
        Assert.Equal(cancelAt - 1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolBoundaryPreservesCancellationWhenDependencyThrows(bool duringTool)
    {
        using var cancellation = new CancellationTokenSource();
        var guard = new EllaiFrameworkToolGuard(2,
            _ =>
            {
                if (!duringTool) { cancellation.Cancel(); throw new InvalidOperationException("synthetic authority failure"); }
                return Task.FromResult(true);
            },
            (_, _, _) => { cancellation.Cancel(); throw new InvalidOperationException("synthetic tool failure"); });
        var context = new EllaiToolContext("session", "epoch", "cases", null, 2, true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.InvokeAsync(context,
            new EllaiToolRequest(EllaiToolNames.Now, new Dictionary<string, string>()), cancellation.Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationDuringFinalAdmissionPreventsProviderDispatch(bool streaming, bool admissionResult)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new AdmissionCountingClient();
        var checks = 0;
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var usage = new InMemoryEllaiUsageStore();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100,
            _ => { if (++checks == 2) { cancellation.Cancel(); return Task.FromResult(admissionResult); } return Task.FromResult(true); },
            (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), usage), cancellation.Token,
            streaming ? (_, _) => ValueTask.CompletedTask : null));
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, (await usage.TotalsAsync("session", default)).Calls);
    }

    private sealed class AdmissionCountingClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task StreamingProviderDisposalErrorIsSanitized()
    {
        using var client = new FailingStreamDisposalClient();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (_, _) => ValueTask.CompletedTask));
        Assert.DoesNotContain("synthetic-disposal-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingProviderDisposalErrorDoesNotOverrideCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new FailingStreamDisposalClient(cancellation);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), cancellation.Token,
            (_, _) => ValueTask.CompletedTask));
        Assert.DoesNotContain("synthetic-disposal-secret", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class FailingStreamDisposalClient(CancellationTokenSource? cancellation = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => new FailingStream(cancellation);
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private sealed class FailingStream(CancellationTokenSource? cancellation) : IAsyncEnumerable<ChatResponseUpdate>, IAsyncEnumerator<ChatResponseUpdate>
        {
            public ChatResponseUpdate Current => new(ChatRole.Assistant,
                [new FunctionCallContent("invalid", "unknown-tool", new Dictionary<string, object?>())]);
            public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync()
            {
                cancellation?.Cancel();
                return ValueTask.FromResult(true);
            }
            public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("synthetic-disposal-secret"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderNetworkErrorAfterCancellationRemainsCancelledAndRecordsUsage(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new CancelThenNetworkErrorClient(cancellation);
        var store = new InMemoryEllaiUsageStore();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), store), cancellation.Token,
            streaming ? (_, _) => ValueTask.CompletedTask : null));
        Assert.Equal(1, (await store.TotalsAsync("session", default)).Calls);
    }

    private sealed class CancelThenNetworkErrorClient(CancellationTokenSource cancellation) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromException<ChatResponse>(new HttpRequestException("synthetic provider disconnect"));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellation.Cancel();
            if (cancellation.IsCancellationRequested) throw new HttpRequestException("synthetic provider disconnect");
            yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledStreamingProviderCannotDeliverLateUpdates(bool emitUnknownTool)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new LateStreamingClient(emitUnknownTool);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var fragments = new List<string>();
        var run = new EllaiFrameworkAgent(client).RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException("No tool may run."),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), cancellation.Token,
            (text, _) => { fragments.Add(text); return ValueTask.CompletedTask; });
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        client.Release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(fragments);
    }

    private sealed class LateStreamingClient(bool emitUnknownTool) : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task;
            if (emitUnknownTool)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("late", "unknown-tool", new Dictionary<string, object?>())]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task MalformedToolCallCannotSupportInventedEvidenceInFinalDecision()
    {
        using var model = new MalformedThenUngroundedClient();
        var agent = new EllaiFrameworkAgent(model);
        var context = new EllaiToolContext("session", "epoch", "public.product-help", null, 10, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.ProductFacts });
        var invoked = 0;
        var result = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "Help")], 256,
            _ => Task.FromResult(true), (_, _, _) => { invoked++; throw new InvalidOperationException(); },
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default);

        Assert.NotNull(result);
        Assert.Equal("Done.", result.DisplayText);
        Assert.True(agent.LastRepairUsed);
        Assert.Equal(0, invoked);
        Assert.Equal(3, model.Calls);
        Assert.Equal(0, model.RepairToolCount);
    }

    private sealed class MalformedThenUngroundedClient : IChatClient
    {
        public int Calls { get; private set; }
        public int RepairToolCount { get; private set; } = -1;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("bad", EllaiToolNames.ProductFacts,
                        new Dictionary<string, object?> { ["unexpected"] = "synthetic" })])));
            if (Calls == 2)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """
                    {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Invented result.","spokenText":"Invented result.",
                    "claims":[{"claimId":"c1","text":"Invented result.","kind":"Observed","evidence":[{"sourceKind":"CaseQueue","resourceId":"fake","version":"1","fieldPath":null,"evidenceId":"fake"}]}],
                    "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                    """)));
            RepairToolCount = options?.Tools?.Count ?? 0;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task FrameworkRepairsMalformedToolArgumentsBeforeBrokerInvocation()
    {
        using var model = new MalformedThenValidToolClient();
        var executor = new EllaiFrameworkAgent(model);
        var context = new EllaiToolContext("session", "epoch", "public.product-help", null, 10, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.ProductFacts });
        var invoked = 0;
        var result = await executor.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "Help")], 256,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                invoked++;
                Assert.Empty(request.Arguments);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "Available", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default);

        Assert.NotNull(result);
        Assert.Equal(1, invoked);
        Assert.Equal(3, model.Calls);
        Assert.False(string.IsNullOrWhiteSpace(model.FirstToolResult));
        Assert.DoesNotContain("synthetic-private-argument", model.FirstToolResult, StringComparison.Ordinal);
    }

    private sealed class MalformedThenValidToolClient : IChatClient
    {
        public int Calls { get; private set; }
        public string FirstToolResult { get; private set; } = string.Empty;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("bad", EllaiToolNames.ProductFacts,
                        new Dictionary<string, object?> { ["unexpected"] = "synthetic-private-argument" })])));
            if (Calls == 2)
            {
                FirstToolResult = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>())
                    .Result?.ToString() ?? string.Empty;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("good", EllaiToolNames.ProductFacts, new Dictionary<string, object?>())])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task FrameworkUsesExistingUiCatalogueAndPreservesGuidanceProposal()
    {
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), uiRegistry: registry);
        using var client = new GuidanceProposalClient();
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "v-test"),
            broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session-1", "epoch-1", "public.product-help", null, 4, true,
            "consent/1", EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools: new HashSet<string> { EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve },
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);

        var result = await executor.RunAsync(new EllaiLoopRequest(context, "Show me how to report a concern",
            new EllaiRoleOverlay("ask", "Be brief."), "report-help", 256, "cycle-1", 1), default);

        Assert.True(result.Completed);
        var proposal = Assert.Single(result.Decision!.UiIntents);
        Assert.Equal(SwiftAid.Contracts.Ellai.UiIntentKind.StartGuidance, proposal.Kind);
        Assert.Equal("report", proposal.Arguments["feature"]);
        Assert.Contains("report.entry", client.Observation);
        Assert.Equal(2, client.Calls);
    }

    private sealed class GuidanceProposalClient : IChatClient
    {
        public int Calls { get; private set; }
        public string Observation { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("discover", EllaiToolNames.UiCatalogue,
                        new Dictionary<string, object?> { ["feature"] = "report", ["goal"] = "report-help" })])));
            Observation = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>())
                .Result?.ToString() ?? string.Empty;
            const string decision = """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Let me show you the report form.",
                 "spokenText":"Let me show you the report form.","claims":[],"questions":[],
                 "uiIntents":[{"kind":"StartGuidance","featureId":"ellai.guidance","resourceId":null,"controlId":null,
                   "arguments":{"feature":"report","objective":"Open the report form"}}],
                 "planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, decision)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task UnsuccessfulRepairsWithZeroReportedUsageStillConsumeTheSessionBudget()
    {
        using var client = new ZeroUsageInvalidClient();
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 2_000);
        var store = new InMemoryEllaiUsageStore();
        var context = new EllaiToolContext("session", "epoch", "cases", null, int.MaxValue, true,
            AllowedTools: new HashSet<string>());
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(), new EllaiFrameworkUsage("session", ledger, store), default));
        Assert.Equal(EllaiToolReasonCodes.BudgetExceeded, error.Code);
        Assert.True(client.Calls > 1);
        var totals = await store.TotalsAsync("session", default);
        Assert.True(totals.TotalTokens > 0);
        Assert.Equal(0, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
    }

    private sealed class ZeroUsageInvalidClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
            { Usage = new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 } });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false, 4001, true)]
    [InlineData(true, 300, true)]
    [InlineData(true, 4001, false)]
    public async Task LongFrameworkRunRetainsOnlyCompleteBoundedProvenance(bool distinctEvidence, int count, bool retained)
    {
        using var client = new LargeToolBatchClient(count);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("policy", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()),
            new EllaiUsageLedger(sessionTokenBudget: 50_000_000), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, int.MaxValue, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Now });
        var calls = 0;
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "test", new EllaiRoleOverlay("ask", "help"),
            "clock", 100, "cycle", 1), default, invokeToolAsync: (_, request, _) =>
            {
                var evidence = distinctEvidence
                    ? new[] { new EvidenceRef("fixture", Interlocked.Increment(ref calls).ToString(), "v1", null, "proof") }
                    : Array.Empty<EvidenceRef>();
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, evidence, "clock", 1, false)
                { HistoryReplayVersion = EllaiObservedToolResult.CurrentClockVersion });
            });
        Assert.True(outcome.Completed);
        Assert.Equal(count, outcome.ToolCallsUsed);
        if (!retained) Assert.Null(outcome.ObservedTools);
        else if (distinctEvidence)
        {
            Assert.Equal(count, outcome.ObservedTools!.Count);
            Assert.NotNull(EllaiToolTrace.SerializeFlattened(outcome.ObservedTools, false, []));
        }
        else
        {
            var observation = Assert.Single(outcome.ObservedTools!);
            Assert.Equal(EllaiObservedToolResult.CurrentClockVersion, observation.ClockVersion);
            Assert.NotNull(EllaiToolTrace.SerializeFlattened(outcome.ObservedTools, false, []));
        }
    }

    [Fact]
    public async Task FrameworkCanInvokeMoreThanThePreviousToolCallCeiling()
    {
        using var client = new LargeToolBatchClient();
        var context = new EllaiToolContext("session", "epoch", "cases", null, int.MaxValue, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Now });
        var calls = 0;
        var decision = await new EllaiFrameworkAgent(client).RunAsync(context, "policy",
            [new ChatMessage(ChatRole.User, "test")], 100, _ => Task.FromResult(true), (_, request, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "clock", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(sessionTokenBudget: 50_000_000), new InMemoryEllaiUsageStore()), default);
        Assert.NotNull(decision);
        Assert.Equal(4_001, calls);
    }

    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 300)]
    public async Task CaseProvenanceCompactsOnlyMatchingSnapshots(bool differentExpirations, int expectedObservations)
    {
        using var client = new LargeToolBatchClient(300, EllaiToolNames.Topic);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("policy", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()),
            new EllaiUsageLedger(sessionTokenBudget: 50_000_000), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, int.MaxValue, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Topic });
        var ids = Enumerable.Range(0, 300).Select(_ => Guid.NewGuid()).ToArray();
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var calls = 0;
        var outcome = await executor.RunAsync(new EllaiLoopRequest(context, "test", new EllaiRoleOverlay("ask", "help"),
            "cases", 100, "cycle", 1), default, invokeToolAsync: (_, request, _) =>
            {
                var index = Interlocked.Increment(ref calls) - 1;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("CaseQueue", ids[index].ToString("D"), "3", null, "proof")], "cases", 1, false)
                    { HistoryValidUntilUtc = expiry.AddSeconds(differentExpirations ? index : 0) });
            });
        Assert.True(outcome.Completed);
        Assert.Equal(300, outcome.ToolCallsUsed);
        if (differentExpirations)
        {
            Assert.Null(outcome.ObservedTools);
            return;
        }
        Assert.Equal(expectedObservations, outcome.ObservedTools!.Count);
        Assert.Equal(ids.Select(id => id.ToString("D")).Order(),
            outcome.ObservedTools.SelectMany(item => item.Evidence).Select(item => item.ResourceId).Order());
        Assert.NotNull(EllaiToolTrace.SerializeFlattened(outcome.ObservedTools, false, []));
    }

    private sealed class LargeToolBatchClient(int count = 4_001, string toolName = EllaiToolNames.Now) : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (++_calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    Enumerable.Range(0, count).Select(index => (AIContent)new FunctionCallContent(
                        $"call-{index}", toolName, toolName == EllaiToolNames.Topic
                            ? new Dictionary<string, object?> { ["ref"] = "case-results" }
                            : new Dictionary<string, object?>())).ToList())));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task FrameworkContinuesPastPreviousIterationAndToolLimits()
    {
        using var client = new LongReasoningClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, int.MaxValue, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Now });
        var calls = 0;
        var decision = await agent.RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                calls++;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "clock", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default);
        Assert.NotNull(decision);
        Assert.Equal(13, calls);
        Assert.Equal(14, client.Calls);
    }

    private sealed class LongReasoningClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Calls <= 13
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{Calls}", EllaiToolNames.Now, new Dictionary<string, object?>())]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task UsageStoreOutageFailsClosedWithoutExposingDiagnosticsOrPublishing()
    {
        using var client = new SplitUsageClient();
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("policy", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(), new FailingUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var chunks = new List<string>();
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => executor.RunAsync(
            new EllaiLoopRequest(context, "hello", new EllaiRoleOverlay("ask", "help"), "chat", 100, "cycle", 1),
            default, onTextDelta: (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; }));
        Assert.Equal("UsageUnavailable", error.Code);
        Assert.DoesNotContain("fixture-secret", error.ToString());
        Assert.Empty(chunks);
    }

    private sealed class FailingUsageStore : IEllaiUsageStore
    {
        public Task RecordAsync(StoredUsageSample sample, CancellationToken ct) => throw new InvalidOperationException("fixture-secret");
        public Task<EllaiUsageTotals> TotalsAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<decimal> SpendForAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<decimal> TotalSpendAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<decimal> BudgetSpendForAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<decimal> TotalBudgetSpendAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StreamingUsageRetainsInputAndOutputReportedInSeparateUpdates()
    {
        using var client = new SplitUsageClient();
        var ledger = new EllaiUsageLedger();
        var store = new InMemoryEllaiUsageStore();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var result = await new EllaiFrameworkAgent(client).RunAsync(context, "policy",
            [new ChatMessage(ChatRole.User, "test")], 100, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", ledger, store), default, (_, _) => ValueTask.CompletedTask);
        Assert.NotNull(result);
        var totals = await store.TotalsAsync("session", default);
        Assert.Equal(7, totals.PromptTokens);
        Assert.Equal(3, totals.CompletionTokens);
        Assert.Equal(10, ledger.TotalsFor("session").TotalTokens);
    }

    private sealed class SplitUsageClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 7 })]);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { OutputTokenCount = 3 })]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameworkRejectsOversizedProviderOutputWithoutPublishing(bool streaming)
    {
        using var client = new OversizedOutputClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var chunks = new List<string>();
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context, "policy",
            [new ChatMessage(ChatRole.User, "test")], 100, _ => Task.FromResult(true), (_, _, _) =>
                throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            streaming ? (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; } : null));
        Assert.Equal("ResponseTooLarge", error.Code);
        Assert.Empty(chunks);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, agent.LastModelCallCount);
    }

    private sealed class OversizedOutputClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new string('x', 512_001))));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Calls++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, new string('x', 300_000));
            yield return new ChatResponseUpdate(ChatRole.Assistant, new string('x', 212_001));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameworkRejectsProviderToolCallWithoutAName(bool streaming)
    {
        using var client = new NamelessToolClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Now });
        var invoked = false;
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context, "policy",
            [new ChatMessage(ChatRole.User, "test")], 100, _ => Task.FromResult(true), (_, _, _) =>
            {
                invoked = true;
                throw new InvalidOperationException();
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            streaming ? (_, _) => ValueTask.CompletedTask : null));
        Assert.Equal(EllaiToolReasonCodes.UnregisteredTool, error.Code);
        Assert.False(invoked);
        Assert.Equal(1, client.Calls);
    }

    private sealed class NamelessToolClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "", new Dictionary<string, object?>())])));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Calls++;
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "", new Dictionary<string, object?>())]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static readonly EllaiCountReplayScope CountScope = EllaiCountReplayScope.Create(30,
        [Guid.Parse("92b34da3-6ea0-41a6-a660-12ea497f86c9"), Guid.Parse("eb298929-c5f5-4ce1-9e53-b8932541c18b")]);
    private const string ValidDecision = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.",
         "spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
         "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

    [Fact]
    public async Task VerifiedCountRejectsScopeForDifferentRequestedWindow()
    {
        using var client = new CountStreamingClient(invalid: true);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.OwnedCaseCount });
        var reference = new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof");
        var calls = 0;
        var result = await agent.RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "count")], 100,
            _ => Task.FromResult(true), (_, request, _) => { calls++; return Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [reference], "2 managed case(s) in scope.", 1, false)
            { CountReplayScope = EllaiCountReplayScope.Create(7, [Guid.NewGuid()]) }); },
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (_, _) => ValueTask.CompletedTask);
        Assert.Null(result);
        Assert.Equal(1, calls);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, agent.LastFailureCode);
    }

    [Fact]
    public async Task VerifiedCountRejectsMalformedRevalidationEvidence()
    {
        using var client = new CountStreamingClient(invalid: true);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.OwnedCaseCount });
        var calls = 0;
        var chunks = new List<string>();
        var decision = await agent.RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "count")], 100,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                var reference = ++calls == 1
                    ? new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof")
                    : new EvidenceRef("Other", "owned-cases", "v1", null, "proof");
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [reference],
                    "2 managed case(s) in scope.", 1, false) { CountReplayScope = CountScope });
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });
        Assert.Null(decision);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, agent.LastFailureCode);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task VerifiedCountIsReturnedWhenModelFinalDecisionIsMalformed()
    {
        using var client = new CountStreamingClient(invalid: true);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.OwnedCaseCount });
        var reference = new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof");
        var calls = 0;
        var decision = await agent.RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "count")], 100,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                calls++;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [reference],
                    "2 managed case(s) in scope.", 1, false) { CountReplayScope = CountScope });
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (_, _) => ValueTask.CompletedTask);
        Assert.Equal(2, calls);
        Assert.Equal("2 managed case(s) in scope.", decision?.DisplayText);
        Assert.False(agent.LastRepairUsed);
    }

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
                true, null, [reference], authorized, 1, false) { CountReplayScope = CountScope }),
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
                    "2 managed case(s) in scope.", 1, false) { CountReplayScope = CountScope };
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
                    "2 managed case(s) in scope.", 1, false) { CountReplayScope = CountScope };
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
                    "2 managed case(s) in scope.", 1, false) { CountReplayScope = CountScope });
            });
        Assert.True(outcome.Completed);
        Assert.Equal(2, calls);
        Assert.Equal(1, outcome.IterationsUsed);
        Assert.Equal(1, outcome.ToolCallsUsed);
        Assert.Single(events, item => item.Kind == EllaiActivityKind.ToolStarted);
        Assert.Single(events, item => item.Kind == EllaiActivityKind.ToolObservationAccepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountWithChangedOrMissingMembershipIsNotPublished(bool missing)
    {
        using var client = new CountStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.OwnedCaseCount });
        var reference = new EvidenceRef("DashboardAggregate", "owned-cases", "v1", null, "proof");
        var chunks = new List<string>();
        var calls = 0;
        var result = await agent.RunAsync(context, "Test policy", [new ChatMessage(ChatRole.User, "Count")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [reference], "2 managed case(s) in scope.", 1, false)
            {
                CountReplayScope = ++calls == 1 ? CountScope : missing ? null : EllaiCountReplayScope.Create(30, [Guid.NewGuid()])
            }), new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });

        Assert.Null(result);
        Assert.Empty(chunks);
        Assert.Equal(2, calls);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, agent.LastFailureCode);
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
                ++calls == 1 ? "2 managed case(s) in scope." : "0 managed case(s) in scope.", 1, false) { CountReplayScope = CountScope }),
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
            true, null, [reference], "2 managed case(s) in scope.", 1, false) { CountReplayScope = CountScope });
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

    private sealed class CountStreamingClient(bool mixed = false, bool invalid = false) : IChatClient
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
                yield return new ChatResponseUpdate(ChatRole.Assistant, invalid ? "{}" : ValidDecision.Replace("Done.", "There are 999 cases.", StringComparison.Ordinal));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ok")]
    public async Task FrameworkToolGuardFailsClosedForDenialWithoutFailureReason(string? reason)
    {
        var guard = new EllaiFrameworkToolGuard(2, _ => Task.FromResult(true),
            (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                false, "denied", [], "", 0, false, reason!)));
        var context = new EllaiToolContext("session", "epoch", "cases", null, 2, true);
        var request = new EllaiToolRequest(EllaiToolNames.Now, new Dictionary<string, string>(), "call");

        await guard.InvokeAsync(context, request, CancellationToken.None);
        var subsequent = await guard.InvokeAsync(context, request, CancellationToken.None);

        Assert.Equal(EllaiToolReasonCodes.Unavailable, guard.FailureCode);
        Assert.False(subsequent.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, subsequent.ReasonCode);
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
        var callIds = new List<string?>();
        var evidence = new EvidenceRef("Document", "safe", "v1", null, "proof-1");
        var decision = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 300,
            _ => Task.FromResult(citeRealEvidence || client.Calls < 5), (_, request, _) =>
            {
                calls.Add(request.ToolName);
                callIds.Add(request.CallId);
                return Task.FromResult(request.ToolName == EllaiToolNames.Now
                    ? new EllaiToolResult(request.ToolName, true, null, [], "Use section safe", 1, false)
                    : new EllaiToolResult(request.ToolName, true, null, [evidence], "Verified observation", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None);

        Assert.Equal(new[] { EllaiToolNames.Now, EllaiToolNames.DocsGet }, calls);
        Assert.Equal(new[] { "first", "second" }, callIds);
        Assert.Equal(citeRealEvidence ? 3 : 5, client.Calls);
        Assert.Contains("Use section safe", client.FirstObservation);
        Assert.Contains("Verified observation", client.SecondObservation);
        Assert.Equal(citeRealEvidence, decision is not null);
    }

    [Theory]
    [InlineData(1, true, 2)]
    [InlineData(3, true, 4)]
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
    public async Task FailedRepairRetainsModelCallCount()
    {
        using var client = new FailedRepairClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>());
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context, "policy",
            [new ChatMessage(ChatRole.User, "test")], 100, _ => Task.FromResult(true), (_, _, _) =>
                throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default));
        Assert.Equal("ProviderUnavailable", error.Code);
        Assert.True(agent.LastRepairUsed);
        Assert.Equal(2, agent.LastModelCallCount);
    }

    [Fact]
    public async Task RepairedDecisionStreamsOnlyTheValidatedAnswer()
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
        Assert.Equal("Done.", string.Concat(fragments));
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
    public async Task ExpiredNoncooperativeToolDoesNotReportPendingAfterDeadline()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(),
            new InMemoryEllaiUsageStore(), TimeSpan.FromMilliseconds(100));
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<EllaiActivityEvent>();
        var run = executor.RunAsync(new EllaiLoopRequest(context, "time?", new EllaiRoleOverlay("ask", "help"),
            "clock", 100, "cycle", 1), CancellationToken.None, onActivity: events.Enqueue,
            invokeToolAsync: async (_, tool, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return new EllaiToolResult(tool.ToolName, true, null, [], "late observation", 1, false);
            });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(8.5));
            Assert.DoesNotContain(events, item => item.Kind == EllaiActivityKind.ToolPending);
        }
        finally { release.TrySetResult(); }
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outcome.Completed);
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
    public async Task CancelledRunDoesNotAcceptLateNoncooperativeToolResult()
    {
        using var client = new ScriptedClient(EllaiToolNames.Now, "unused", "example", ValidDecision, noArguments: true);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Policy text.", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<EllaiActivityEvent>();
        using var cancellation = new CancellationTokenSource();
        var run = executor.RunAsync(new EllaiLoopRequest(context, "time?", new EllaiRoleOverlay("ask", "help"),
            "clock", 100, "cycle", 1), cancellation.Token, onActivity: events.Add,
            invokeToolAsync: async (_, tool, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return new EllaiToolResult(tool.ToolName, true, null, [], "late observation", 1, false);
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(events, item => item.Kind == EllaiActivityKind.ToolObservationAccepted);
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
    public async Task InFlightFrameworkStreamRetainsEstimatedBudgetAfterCancellation()
    {
        using var client = new BlockingStreamingClient();
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 15_000);
        var store = new InMemoryEllaiUsageStore();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal));
        using var cancellation = new CancellationTokenSource();
        Task<EllaiDecision?> RunAsync(CancellationToken token) => new EllaiFrameworkAgent(client).RunAsync(
            context, "policy", [new ChatMessage(ChatRole.User, "hello")], 10_000,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", ledger, store), token, (_, _) => ValueTask.CompletedTask);

        var first = RunAsync(cancellation.Token);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => RunAsync(default));
        Assert.Equal(EllaiToolReasonCodes.BudgetExceeded, failure.Code);
        Assert.Equal(1, client.Calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        var totals = await store.TotalsAsync("session", default);
        Assert.Equal(1, totals.Calls);
        Assert.True(totals.TotalTokens >= 10_000);
        using var reservation = ledger.TryBeginModelCall("session", 15_000);
        Assert.Null(reservation);
    }

    [Fact]
    public async Task ProviderUsageIsPersistedWhenCallerCancelsAfterResponse()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new CancelAfterResponseClient(cancellation);
        var ledger = new EllaiUsageLedger();
        var store = new CancellationAwareUsageStore();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EllaiFrameworkAgent(client).RunAsync(context, "policy",
                [new ChatMessage(ChatRole.User, "hello")], 100, _ => Task.FromResult(true),
                (_, _, _) => throw new InvalidOperationException(), new EllaiFrameworkUsage("session", ledger, store),
                cancellation.Token));
        var totals = await store.TotalsAsync("session", default);
        Assert.Equal(1, totals.Calls);
        Assert.Equal(7, totals.PromptTokens);
        Assert.Equal(3, totals.CompletionTokens);
    }

    [Fact]
    public async Task ProviderFailureRetainsEstimatedBudget()
    {
        using var client = new FailingClient();
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 100_000);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal));
        await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context, "policy",
            [new ChatMessage(ChatRole.User, "hello")], 100, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", ledger, new InMemoryEllaiUsageStore()), default));
        using var fullBudget = ledger.TryBeginModelCall("session", 100_000);
        Assert.Null(fullBudget);
        Assert.True(ledger.TotalsFor("session").TotalTokens >= 100);
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
        var evidence = new EvidenceRef("TestSource", "synthetic-reference", "1", null, "proof");
        var outcome = await executor.RunAsync(request, CancellationToken.None, onActivity: events.Add,
            invokeToolAsync: (_, tool, _) => Task.FromResult(new EllaiToolResult(tool.ToolName, true, null, [evidence], "observation", 1, false)));
        Assert.True(outcome.Completed);
        Assert.Equal("Done.", outcome.Decision?.DisplayText);
        Assert.Equal(2, outcome.IterationsUsed);
        Assert.Equal(1, outcome.ToolCallsUsed);
        var observed = Assert.Single(outcome.ObservedTools!);
        Assert.Equal(EllaiToolNames.Now, observed.ToolName);
        Assert.True(observed.Allowed);
        Assert.Equal(evidence, Assert.Single(observed.Evidence));
        Assert.False(observed.Truncated);
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
        string? callId = null;
        var fragments = new List<string>();
        var decision = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                invoked++;
                callId = request.CallId;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "observation", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), store), CancellationToken.None,
            (text, _) => { fragments.Add(text); return ValueTask.CompletedTask; });
        Assert.Equal(1, invoked);
        Assert.Equal("call-1", callId);
        Assert.Equal(2, client.Calls);
        Assert.Equal("Done.", decision?.DisplayText);
        Assert.Equal("Done.", string.Concat(fragments));
        Assert.Equal(2, (await store.TotalsAsync("session", CancellationToken.None)).Calls);
    }

    [Fact]
    public async Task TruncatedNonStreamingResponseRetriesWithoutExecutingItsToolCall()
    {
        using var client = new TruncatedNonStreamingClient();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var usage = new InMemoryEllaiUsageStore();
        var calls = 0;
        var decision = await new EllaiFrameworkAgent(client).RunAsync(context, "Test instructions",
            [new ChatMessage(ChatRole.User, "test")], 100, _ => Task.FromResult(true), (_, request, _) =>
            {
                calls++;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "observation", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), usage), default);

        Assert.Equal("Done.", decision?.DisplayText);
        Assert.Equal(new int?[] { 100, 200 }, client.OutputLimits);
        Assert.Equal(0, calls);
        Assert.Equal(2, (await usage.TotalsAsync("session", default)).Calls);
    }

    [Fact]
    public async Task TruncatedNonStreamingRetryStopsAfterSecondTruncation()
    {
        using var client = new TruncatedNonStreamingClient(alwaysTruncated: true);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var usage = new InMemoryEllaiUsageStore();
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), usage), default));
        Assert.Equal("ProviderOutputLimit", error.Code);
        Assert.Equal(new int?[] { 100, 200 }, client.OutputLimits);
        Assert.Equal(2, (await usage.TotalsAsync("session", default)).Calls);
    }

    [Fact]
    public async Task TruncatedNonStreamingRetryRechecksAuthorization()
    {
        var authorized = true;
        using var client = new TruncatedNonStreamingClient(onFirstCall: () => authorized = false);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var usage = new InMemoryEllaiUsageStore();
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(authorized), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), usage), default));
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, error.Code);
        Assert.Single(client.OutputLimits);
        Assert.Equal(1, (await usage.TotalsAsync("session", default)).Calls);
    }

    private sealed class TruncatedNonStreamingClient(bool alwaysTruncated = false, Action? onFirstCall = null) : IChatClient
    {
        public List<int?> OutputLimits { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            OutputLimits.Add(options?.MaxOutputTokens);
            if (OutputLimits.Count == 1) onFirstCall?.Invoke();
            return Task.FromResult(OutputLimits.Count == 1 || alwaysTruncated
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("truncated", EllaiToolNames.Now, new Dictionary<string, object?>())]))
                    { FinishReason = ChatFinishReason.Length }
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task TruncatedStreamingToolCallIsNotExecutedOrPublished()
    {
        using var client = new TruncatedToolStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var usageStore = new InMemoryEllaiUsageStore();
        var calls = 0;
        var chunks = new List<string>();
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context,
            "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                calls++;
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "observation", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), usageStore), CancellationToken.None,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; }));

        Assert.Equal("ProviderOutputLimit", error.Code);
        Assert.Equal(0, calls);
        Assert.Empty(chunks);
        Assert.Equal(1, (await usageStore.TotalsAsync("session", CancellationToken.None)).Calls);
    }

    private sealed class TruncatedToolStreamingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("truncated-call", EllaiToolNames.Now, new Dictionary<string, object?>())]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Length };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
    public async Task VerifiedSegmentArrivesBeforeProviderCompletes()
    {
        using var client = new VerifiedSegmentStreamingClient();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal));
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
            onVerifiedStreamSegment: (text, _) => { received.TrySetResult(text); return ValueTask.CompletedTask; });
        try
        {
            Assert.Equal("Done.", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(run.IsCompleted);
        }
        finally { client.Release.TrySetResult(); }
        Assert.Equal("Done.", (await run)?.DisplayText);
    }

    private sealed class VerifiedSegmentStreamingClient : IChatClient
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "{\"stream_segments\":[{\"text\":\"Done.\",\"claims\":[]}]");
            await Release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "," + ValidDecision.TrimStart()[1..]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrivateToolSegmentRequiresFreshAuthorization(bool authorized)
    {
        using var client = new PrivateSegmentStreamingClient();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.State });
        var published = new List<string>();
        var checks = 0;
        var agent = new EllaiFrameworkAgent(client);
        var run = agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [], "fixture", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
            onVerifiedStreamSegment: (text, _) => { published.Add(text); return ValueTask.CompletedTask; },
            revalidateStreamObservations: _ => { checks++; return Task.FromResult(authorized); });
        try
        {
            if (!authorized)
            {
                var failure = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(async () => await run);
                Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, failure.Code);
                Assert.Empty(published);
                return;
            }
            await client.SegmentSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, checks);
            Assert.Equal(new[] { "Done." }, published);
        }
        finally { client.Release.TrySetResult(); }
        Assert.Equal("Done.", (await run)?.DisplayText);
    }

    private sealed class PrivateSegmentStreamingClient(bool omitSegment = false, string? segmentPrefix = null, string? finalDecision = null) : IChatClient
    {
        private int _calls;
        public TaskCompletionSource SegmentSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++_calls == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("private", EllaiToolNames.State, new Dictionary<string, object?> { ["projection"] = "queue" })]);
                yield break;
            }
            if (omitSegment)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision);
                yield break;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, segmentPrefix ?? "{\"stream_segments\":[{\"text\":\"Done.\",\"claims\":[]}]");
            SegmentSent.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "," + (finalDecision ?? ValidDecision).TrimStart()[1..]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task RevokedPrivateEvidenceCannotReachBufferedFinalAnswer()
    {
        using var client = new PrivateSegmentStreamingClient(omitSegment: true);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.State });
        var agent = new EllaiFrameworkAgent(client);
        var published = new List<string>();
        var result = await agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [], "fixture", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
            onTextDelta: (text, _) => { published.Add(text); return ValueTask.CompletedTask; },
            revalidateStreamObservations: _ => Task.FromResult(false));
        Assert.Null(result);
        Assert.Empty(published);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, agent.LastFailureCode);
    }

    [Fact]
    public async Task EvidenceBackedSegmentWithoutClaimsIsNotStreamed()
    {
        using var client = new PrivateSegmentStreamingClient();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.State });
        var agent = new EllaiFrameworkAgent(client);
        var published = new List<string>();
        var evidence = new EvidenceRef("CaseQueue", "case", "1", null, "case");
        var run = agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [evidence], "fixture", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
            onVerifiedStreamSegment: (text, _) => { published.Add(text); return ValueTask.CompletedTask; },
            revalidateStreamObservations: _ => Task.FromResult(true));
        try
        {
            await client.SegmentSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(published);
        }
        finally { client.Release.TrySetResult(); }
        Assert.Equal("Done.", (await run)?.DisplayText);
    }

    [Theory]
    [InlineData("Done.", true)]
    [InlineData("A different observation.", false)]
    [InlineData("Not Done.", false)]
    public async Task EvidenceBackedSegmentRequiresVerbatimSource(string source, bool expected)
    {
        const string claim = "{\"claimId\":\"claim\",\"text\":\"Done.\",\"kind\":\"Observed\",\"evidence\":[{\"sourceKind\":\"CaseQueue\",\"resourceId\":\"case\",\"version\":\"1\",\"fieldPath\":null,\"evidenceId\":\"case\"}]}";
        using var client = new PrivateSegmentStreamingClient(segmentPrefix: "{\"stream_segments\":[{\"text\":\"Done.\",\"claims\":[" + claim + "]}]",
            finalDecision: ValidDecision.Replace("\"claims\":[]", "\"claims\":[" + claim + "]", StringComparison.Ordinal));
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.State });
        var published = new List<string>();
        var evidence = new EvidenceRef("CaseQueue", "case", "1", null, "case");
        var agent = new EllaiFrameworkAgent(client);
        var run = agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [evidence], source, 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
            onVerifiedStreamSegment: (text, _) => { published.Add(text); return ValueTask.CompletedTask; },
            revalidateStreamObservations: _ => Task.FromResult(true));
        try
        {
            await client.SegmentSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected ? new[] { "Done." } : Array.Empty<string>(), published);
        }
        finally { client.Release.TrySetResult(); }
        Assert.Equal("Done.", (await run)?.DisplayText);
    }

    [Fact]
    public async Task RevocationBeforeFinalDecisionStopsCompletion()
    {
        using var client = new PrivateSegmentStreamingClient();
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.State });
        var checks = 0;
        var agent = new EllaiFrameworkAgent(client);
        var run = agent.RunAsync(context, "Test instructions", [new ChatMessage(ChatRole.User, "test")], 100,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [], "fixture", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), CancellationToken.None,
            onVerifiedStreamSegment: (_, _) => ValueTask.CompletedTask,
            revalidateStreamObservations: _ => Task.FromResult(++checks == 1));
        try { await client.SegmentSent.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { client.Release.TrySetResult(); }
        Assert.Null(await run);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, agent.LastFailureCode);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatedSentenceChunksPreserveTextAndStopWhenSessionChanges(bool revokeAfterFirst)
    {
        var first = new string('a', 80) + ".";
        var answer = first + " Second sentence. Third sentence.";
        using var client = new SentenceStreamingClient(answer);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, int.MaxValue, true,
            AllowedTools: new HashSet<string>());
        var current = true;
        var chunks = new List<string>();
        var result = await agent.RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100,
            _ => Task.FromResult(current), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); if (revokeAfterFirst) current = false; return ValueTask.CompletedTask; });
        if (revokeAfterFirst)
        {
            Assert.Null(result);
            Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, agent.LastFailureCode);
            Assert.Equal(first, Assert.Single(chunks));
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(3, chunks.Count);
            Assert.Equal(answer, string.Join(" ", chunks));
        }
    }

    [Fact]
    public async Task ShortValidatedSentencesProduceSeparateReconstructableChunks()
    {
        const string answer = "Welcome to SwiftAid. Your observations help your community. You decide when to submit your report.";
        using var client = new SentenceStreamingClient(answer);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, int.MaxValue, true,
            AllowedTools: new HashSet<string>());
        var chunks = new List<string>();
        var result = await agent.RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "hello")], 100,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });
        Assert.NotNull(result);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(answer, string.Join(" ", chunks));
    }

    private sealed class SentenceStreamingClient(string answer) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, ValidDecision.Replace("Done.", answer, StringComparison.Ordinal));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInsidePublicationStopsTheFrameworkTurn(bool multipleChunks)
    {
        var answer = multipleChunks ? new string('a', 80) + ". Second sentence." : "A short answer.";
        using var client = new SentenceStreamingClient(answer);
        using var cancellation = new CancellationTokenSource();
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, int.MaxValue, true,
            AllowedTools: new HashSet<string>());
        var chunks = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync(context, "policy",
            [new ChatMessage(ChatRole.User, "hello")], 100, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), cancellation.Token,
            (text, _) => { chunks.Add(text); cancellation.Cancel(); return ValueTask.CompletedTask; }));
        Assert.Single(chunks);
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

    private sealed class BlockingStreamingClient : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CancelAfterResponseClient(CancellationTokenSource cancellation) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidDecision))
            {
                Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }
            });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CancellationAwareUsageStore : IEllaiUsageStore
    {
        private readonly InMemoryEllaiUsageStore _inner = new();
        public Task RecordAsync(StoredUsageSample sample, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return _inner.RecordAsync(sample, ct);
        }
        public Task<EllaiUsageTotals> TotalsAsync(string sessionId, CancellationToken ct) => _inner.TotalsAsync(sessionId, ct);
        public Task<decimal> SpendForAsync(string sessionId, CancellationToken ct) => _inner.SpendForAsync(sessionId, ct);
        public Task<decimal> TotalSpendAsync(CancellationToken ct) => _inner.TotalSpendAsync(ct);
        public Task<decimal> BudgetSpendForAsync(string sessionId, CancellationToken ct) => _inner.BudgetSpendForAsync(sessionId, ct);
        public Task<decimal> TotalBudgetSpendAsync(CancellationToken ct) => _inner.TotalBudgetSpendAsync(ct);
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

    private sealed class FailedRepairClient : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (++_calls > 1) throw new InvalidOperationException("provider failed");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
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
