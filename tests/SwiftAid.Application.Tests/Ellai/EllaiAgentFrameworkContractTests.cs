using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiAgentFrameworkContractTests
{
    private const string ValidDecision = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.",
         "spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
         "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

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
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(context, "Test instructions",
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

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1)
            {
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
