using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiFrameworkSessionCodecTests
{
    [Fact]
    public async Task RestoreReplacesSavedHistoryWithCurrentlyAuthorizedHistory()
    {
        using var client = new CaptureClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "Ellai",
            ChatHistoryProvider = new InMemoryChatHistoryProvider()
        });
        var session = await agent.CreateSessionAsync();
        session.SetInMemoryChatHistory([new ChatMessage(ChatRole.User, "revoked private history")]);
        var saved = await EllaiFrameworkSessionCodec.SerializeAsync(agent, session, "session-1", "owner-1", "scope-1", default);

        var restored = await EllaiFrameworkSessionCodec.RestoreAsync(agent, saved, "session-1", "owner-1", "scope-1",
            [new ChatMessage(ChatRole.User, "authorized history")], default);
        await agent.RunAsync(new ChatMessage(ChatRole.User, "current request"), restored);

        Assert.Contains(client.Messages, message => message.Text == "authorized history");
        Assert.Contains(client.Messages, message => message.Text == "current request");
        Assert.DoesNotContain(client.Messages, message => message.Text == "revoked private history");
    }

    [Theory]
    [InlineData("other-session", "owner-1", "scope-1")]
    [InlineData("session-1", "other-owner", "scope-1")]
    [InlineData("session-1", "owner-1", "other-scope")]
    public async Task RestoreRejectsDifferentSessionOrOwner(string sessionId, string ownerId, string scopeEpoch)
    {
        using var client = new CaptureClient();
        AIAgent agent = client.AsAIAgent();
        var session = await agent.CreateSessionAsync();
        var saved = await EllaiFrameworkSessionCodec.SerializeAsync(agent, session, "session-1", "owner-1", "scope-1", default);
        await Assert.ThrowsAsync<InvalidDataException>(() => EllaiFrameworkSessionCodec.RestoreAsync(
            agent, saved, sessionId, ownerId, scopeEpoch, [], default));
    }

    [Fact]
    public async Task ChangedScopeRequiresNewFrameworkSession()
    {
        using var client = new CaptureClient();
        AIAgent agent = client.AsAIAgent();
        var saved = await EllaiFrameworkSessionCodec.SerializeAsync(agent, await agent.CreateSessionAsync(),
            "session-1", "owner-1", "scope-1", default);
        Assert.True(EllaiFrameworkSessionCodec.IsCurrentScope(saved, "session-1", "owner-1", "scope-1"));
        Assert.False(EllaiFrameworkSessionCodec.IsCurrentScope(saved, "session-1", "owner-1", "scope-2"));
        Assert.Throws<InvalidDataException>(() =>
            EllaiFrameworkSessionCodec.IsCurrentScope(saved, "session-1", "other-owner", "scope-1"));
    }

    private sealed class CaptureClient : IChatClient
    {
        public List<ChatMessage> Messages { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
