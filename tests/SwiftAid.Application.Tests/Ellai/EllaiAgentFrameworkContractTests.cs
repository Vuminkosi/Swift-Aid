using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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

    private sealed class ScriptedClient : IChatClient
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
                Assert.Contains(options!.Tools!, tool => tool.Name == "lookup");
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["value"] = "example" })])));
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
