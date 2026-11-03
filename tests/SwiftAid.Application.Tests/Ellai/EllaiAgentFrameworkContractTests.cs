using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiAgentFrameworkContractTests
{
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

    private sealed class ScriptedClient(string toolName = "lookup", string argumentName = "value", string argumentValue = "example") : IChatClient
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
                    [new FunctionCallContent("call-1", toolName, new Dictionary<string, object?> { [argumentName] = argumentValue })])));
            }

            ToolResult = string.Join(" ", messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Select(content => content.Result?.ToString()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
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
}
