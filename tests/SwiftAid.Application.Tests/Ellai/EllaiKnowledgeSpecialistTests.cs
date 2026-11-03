using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiKnowledgeSpecialistTests
{
    [Fact]
    public async Task FrameworkRunUsesOnlyParentAuthorizedDocumentationAndReturnsObservedEvidence()
    {
        using var model = new DocumentationClient();
        var specialist = new EllaiKnowledgeSpecialist(model);
        var parent = Context();
        var calls = 0;
        var result = await specialist.RunAsync("Find the synthetic reference", "Trusted policy", true, parent,
            Usage(), _ => Task.FromResult(true), (context, request, _) =>
            {
                calls++;
                Assert.Equal(parent.SessionId, context.SessionId);
                Assert.Equal(parent.ScopeEpoch, context.ScopeEpoch);
                Assert.Equal(EllaiToolNames.DocsGet, request.ToolName);
                Assert.DoesNotContain(EllaiToolNames.State, context.AllowedTools!);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("Documentation", "safe", "v1", null, "proof-1")], "Observed.", 1, false));
            }, default);

        Assert.Equal(EllaiSpecialistOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Decision);
        Assert.Equal(1, calls);
        Assert.Equal(new[] { EllaiToolNames.DocsGet }, model.AvailableTools);
    }

    [Fact]
    public async Task DisabledOrMismatchedSessionNeverCallsProvider()
    {
        using var model = new DocumentationClient();
        var specialist = new EllaiKnowledgeSpecialist(model);
        static Task<EllaiToolResult> Invoke(EllaiToolContext _, EllaiToolRequest __, CancellationToken ___) =>
            throw new InvalidOperationException();
        var disabled = await specialist.RunAsync("question", "policy", false, Context(), Usage(),
            _ => Task.FromResult(true), Invoke, default);
        var otherSession = new EllaiFrameworkUsage("other", new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var mismatch = await specialist.RunAsync("question", "policy", true, Context(), otherSession,
            _ => Task.FromResult(true), Invoke, default);
        Assert.Equal(EllaiSpecialistOutcome.Denied, disabled.Outcome);
        Assert.Equal(EllaiSpecialistOutcome.Denied, mismatch.Outcome);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task RevokedSessionStopsBeforeProviderOrTool()
    {
        using var model = new DocumentationClient();
        var specialist = new EllaiKnowledgeSpecialist(model);
        var result = await specialist.RunAsync("question", "policy", true, Context(), Usage(),
            _ => Task.FromResult(false), (_, _, _) => throw new InvalidOperationException(), default);
        Assert.Equal(EllaiSpecialistOutcome.Unverified, result.Outcome);
        Assert.Null(result.Decision);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task InvalidModelResponsesStopAtSpecialistModelBudget()
    {
        using var model = new InvalidClient();
        var specialist = new EllaiKnowledgeSpecialist(model);
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() =>
            specialist.RunAsync("question", "policy", true, Context(), Usage(),
                _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(), default));
        Assert.Equal(EllaiToolReasonCodes.BudgetExceeded, error.Code);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task NonDocumentationEvidenceCannotProduceKnowledgeResult()
    {
        using var model = new DocumentationClient("CaseQueue");
        var specialist = new EllaiKnowledgeSpecialist(model);
        var result = await specialist.RunAsync("question", "policy", true, Context(), Usage(),
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(new EllaiToolResult(request.ToolName,
                true, null, [new EvidenceRef("CaseQueue", "safe", "v1", null, "proof-1")], "Observed.", 1, false)), default);
        Assert.Equal(EllaiSpecialistOutcome.Unverified, result.Outcome);
        Assert.Null(result.Decision);
    }

    [Fact]
    public async Task FrameworkSpecialistUsesRealBrokerConsentAndDocumentRevision()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ellai-specialist-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(path, "# Reference\n\n**Status: Approved.**\n\n## 1. Safe\n\nObserved.\n");
            var docs = new EllaiTechnicalDocs(path);
            var consent = new EllaiConsentService();
            consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
                EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
            var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, docs);
            using var model = new DocumentationClient("Documentation", "1-safe", docs.Revision(), "1-safe");
            var parent = Context() with { DataClassification = EllaiProcessingClassification.SyntheticDemo };
            var result = await new EllaiKnowledgeSpecialist(model).RunAsync("Read the reference", "Trusted policy",
                true, parent, Usage(), _ => Task.FromResult(true), broker.InvokeAsync, default);
            Assert.Equal(EllaiSpecialistOutcome.Completed, result.Outcome);
            Assert.Equal(docs.Revision(), Assert.Single(Assert.Single(result.Decision!.Claims).Evidence).Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SupervisorDelegatesAndRetainsSpecialistEvidenceUnderParentGuard()
    {
        using var model = new DelegationClient();
        var context = Context();
        var calls = 0;
        var agent = new EllaiFrameworkAgent(model);
        var result = await agent.RunAsync(context, "Trusted policy", [new ChatMessage(ChatRole.User, "Find documentation")],
            512, _ => Task.FromResult(true), (toolContext, request, _) =>
            {
                calls++;
                Assert.Equal(context.SessionId, toolContext.SessionId);
                Assert.Equal(EllaiToolNames.DocsGet, request.ToolName);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null,
                    [new EvidenceRef("Documentation", "safe", "v1", null, "proof-1")], "Observed.", 1, false));
            }, Usage(), default, enableKnowledgeSpecialist: true);

        Assert.NotNull(result);
        Assert.Equal("proof-1", Assert.Single(Assert.Single(result.Claims).Evidence).EvidenceId);
        Assert.Equal(1, calls);
        Assert.Equal(4, model.Calls);
        Assert.Equal(4, agent.LastModelCallCount);
    }

    [Fact]
    public async Task SupervisorDoesNotAdvertiseDelegationByDefault()
    {
        using var model = new DirectClient();
        var result = await new EllaiFrameworkAgent(model).RunAsync(Context(), "policy",
            [new ChatMessage(ChatRole.User, "hello")], 512, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException(), Usage(), default);
        Assert.NotNull(result);
        Assert.DoesNotContain("ellai_delegate_knowledge", model.AvailableTools);
    }

    [Fact]
    public async Task ExecutorIncludesDelegatedCallsAndObservationsInParentOutcome()
    {
        using var model = new DelegationClient();
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Policy", "test"),
            new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService()),
            new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), enableKnowledgeSpecialist: true);
        var request = new EllaiLoopRequest(Context(), "Find documentation", new EllaiRoleOverlay("ask", "help"),
            "help", 512, "cycle", 1);
        var result = await executor.RunAsync(request, default, invokeToolAsync: (_, tool, _) =>
            Task.FromResult(new EllaiToolResult(tool.ToolName, true, null,
                [new EvidenceRef("Documentation", "safe", "v1", null, "proof-1")], "Observed.", 1, false)));

        Assert.True(result.Completed);
        Assert.Equal(4, result.IterationsUsed);
        Assert.Equal(1, result.ToolCallsUsed);
        Assert.Equal(EllaiToolNames.DocsGet, Assert.Single(result.ObservedTools!).ToolName);
    }

    private static EllaiToolContext Context() => new("session", "epoch", "public.product-help", null, 3, true,
        AllowedTools: new HashSet<string> { EllaiToolNames.DocsGet, EllaiToolNames.State });

    private static EllaiFrameworkUsage Usage() => new("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore());

    private sealed class InvalidClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "invalid")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DelegationClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("delegate", "ellai_delegate_knowledge", new Dictionary<string, object?> { ["task"] = "Find the reference" })])));
            if (Calls == 2)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("lookup", EllaiToolNames.DocsGet, new Dictionary<string, object?> { ["section"] = "safe" })])));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Observed.","spokenText":"Observed.",
                "claims":[{"claimId":"c1","text":"Observed.","kind":"Observed","evidence":[{"sourceKind":"Documentation","resourceId":"safe","version":"v1","fieldPath":null,"evidenceId":"proof-1"}]}],
                "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DirectClient : IChatClient
    {
        public string[] AvailableTools { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            AvailableTools = options!.Tools!.OfType<AIFunctionDeclaration>().Select(tool => tool.Name).ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Hello.","spokenText":"Hello.",
                "claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DocumentationClient(string sourceKind = "Documentation", string section = "safe",
        string version = "v1", string evidenceId = "proof-1") : IChatClient
    {
        public int Calls { get; private set; }
        public string[] AvailableTools { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
            {
                AvailableTools = options!.Tools!.OfType<AIFunctionDeclaration>().Select(tool => tool.Name).ToArray();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("lookup", EllaiToolNames.DocsGet, new Dictionary<string, object?> { ["section"] = section })])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Observed.","spokenText":"Observed.",
                "claims":[{"claimId":"c1","text":"Observed.","kind":"Observed","evidence":[{"sourceKind":"SOURCE_KIND","resourceId":"SECTION_ID","version":"VERSION_ID","fieldPath":null,"evidenceId":"EVIDENCE_ID"}]}],
                "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """.Replace("SOURCE_KIND", sourceKind, StringComparison.Ordinal)
                    .Replace("SECTION_ID", section, StringComparison.Ordinal)
                    .Replace("VERSION_ID", version, StringComparison.Ordinal)
                    .Replace("EVIDENCE_ID", evidenceId, StringComparison.Ordinal))));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
