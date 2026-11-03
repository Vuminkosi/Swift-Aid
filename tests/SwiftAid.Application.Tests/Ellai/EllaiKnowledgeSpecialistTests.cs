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
                    [new EvidenceRef("Document", "safe", "v1", null, "proof-1")], "Observed.", 1, false));
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

    private static EllaiToolContext Context() => new("session", "epoch", "public.product-help", null, 3, true,
        AllowedTools: new HashSet<string> { EllaiToolNames.DocsGet, EllaiToolNames.State });

    private static EllaiFrameworkUsage Usage() => new("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore());

    private sealed class DocumentationClient : IChatClient
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
                    [new FunctionCallContent("lookup", EllaiToolNames.DocsGet, new Dictionary<string, object?> { ["section"] = "safe" })])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """
                {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Observed.","spokenText":"Observed.",
                "claims":[{"claimId":"c1","text":"Observed.","kind":"Observed","evidence":[{"sourceKind":"Document","resourceId":"safe","version":"v1","fieldPath":null,"evidenceId":"proof-1"}]}],
                "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                """)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
