using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>A bounded single-agent execution building block. Gateway integration
/// must supply trusted instructions, durable history, usage and lifecycle fencing.</summary>
public sealed class EllaiFrameworkAgent(IChatClient client)
{
    private readonly IChatClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<EllaiDecision?> RunAsync(
        EllaiToolContext context, string instructions, IReadOnlyList<ChatMessage> messages, int maxOutputTokens,
        Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        EllaiFrameworkUsage usage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentNullException.ThrowIfNull(usage);
        if (!string.Equals(context.SessionId, usage.SessionId, StringComparison.Ordinal))
            throw new ArgumentException("Usage must belong to the current session.", nameof(usage));
        if (messages.Count == 0 || maxOutputTokens is < 1 or > 16384)
            throw new ArgumentException("Messages and a bounded token limit are required.");
        if (context.RemainingCalls <= 0 || !await isCurrent(ct).ConfigureAwait(false)) return null;

        var evidence = new HashSet<EvidenceRef>();
        var guard = new EllaiFrameworkToolGuard(Math.Min(context.RemainingCalls, EllaiCycleRunner.MaxToolCalls),
            isCurrent, async (toolContext, request, token) =>
            {
                var result = await invoke(toolContext, request, token).ConfigureAwait(false);
                if (result.Allowed)
                    foreach (var reference in result.Evidence.Take(20)) evidence.Add(reference);
                return result;
            });
        var tools = EllaiFrameworkToolCatalog.Create(context, guard.InvokeAsync);
        var guardedClient = new ModelCallGuard(_client, isCurrent, usage);
        var functionClient = new FunctionInvokingChatClient(guardedClient)
        {
            MaximumIterationsPerRequest = EllaiCycleRunner.MaxIterations
        };
        AIAgent agent = functionClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "Ellai",
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools.ToList(), MaxOutputTokens = maxOutputTokens }
        });
        var session = await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = await agent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
        if (guard.FailureCode is not null || !await isCurrent(ct).ConfigureAwait(false)) return null;

        var decision = EllaiModelTurnParser.Parse(response.Text).Turn?.FinalDecision;
        if (decision is null || EllaiDecisionValidator.Validate(decision).Count != 0) return null;
        if (decision.Claims.Any(claim => claim?.Evidence is { } refs && refs.Any(reference => reference is null || !evidence.Contains(reference))))
            return null;
        return decision;
    }

    private sealed class ModelCallGuard(IChatClient inner, Func<CancellationToken, Task<bool>> isCurrent,
        EllaiFrameworkUsage usage) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!await isCurrent(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The Ellai session is no longer authorized.");
            var boundedMessages = messages.ToArray();
            if (!usage.CanDispatch(boundedMessages, options))
                throw new InvalidOperationException("The Ellai session budget is exhausted.");
            var response = await base.GetResponseAsync(boundedMessages, options, cancellationToken).ConfigureAwait(false);
            await usage.RecordAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Streaming requires equivalent authorization and usage handling.");
    }
}
