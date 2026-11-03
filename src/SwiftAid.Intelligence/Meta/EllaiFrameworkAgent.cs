using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>A bounded single-agent execution building block. Gateway integration
/// must supply trusted instructions, durable history, usage and lifecycle fencing.</summary>
public sealed class EllaiFrameworkAgent(IChatClient client)
{
    private readonly IChatClient _client = client ?? throw new ArgumentNullException(nameof(client));
    public int LastModelCallCount { get; private set; }

    public async Task<EllaiDecision?> RunAsync(
        EllaiToolContext context, string instructions, IReadOnlyList<ChatMessage> messages, int maxOutputTokens,
        Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        EllaiFrameworkUsage usage,
        CancellationToken ct, Func<string, CancellationToken, ValueTask>? onTextDelta = null)
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
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools.ToList(), MaxOutputTokens = maxOutputTokens }
        });
        var session = await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
        session.SetInMemoryChatHistory(messages.Take(messages.Count - 1).ToList());
        var currentMessage = messages[^1];
        EllaiDecisionTextStreamParser? parser = null;
        if (onTextDelta is null)
        {
            await agent.RunAsync(currentMessage, session, cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            parser = new EllaiDecisionTextStreamParser();
            var textLength = 0;
            var parserCall = 0;
            await foreach (var update in agent.RunStreamingAsync(currentMessage, session, cancellationToken: ct).ConfigureAwait(false))
            {
                if (parserCall != guardedClient.CallSequence)
                {
                    parser = new EllaiDecisionTextStreamParser();
                    parserCall = guardedClient.CallSequence;
                }
                textLength += update.Text.Length;
                if (textLength > 512_000) throw new InvalidOperationException("Ellai response exceeded its stream limit.");
                foreach (var sentence in parser.Append(update.Text))
                {
                    if (guard.FailureCode is null && await isCurrent(ct).ConfigureAwait(false))
                        await onTextDelta(sentence, ct).ConfigureAwait(false);
                }
            }
        }
        LastModelCallCount = guardedClient.CallSequence;
        if (guard.FailureCode is not null || !await isCurrent(ct).ConfigureAwait(false)) return null;

        var decision = EllaiModelTurnParser.Parse(guardedClient.LastText).Turn?.FinalDecision;
        if (decision is null || EllaiDecisionValidator.Validate(decision).Count != 0) return null;
        if (decision.Claims.Any(claim => claim?.Evidence is { } refs && refs.Any(reference => reference is null || !evidence.Contains(reference))))
            return null;
        if (parser is not null && onTextDelta is not null)
        {
            var remainder = parser.FlushVerified(decision.DisplayText);
            if (remainder.Length > 0 && await isCurrent(ct).ConfigureAwait(false))
                await onTextDelta(remainder, ct).ConfigureAwait(false);
        }
        return decision;
    }

    private sealed class ModelCallGuard(IChatClient inner, Func<CancellationToken, Task<bool>> isCurrent,
        EllaiFrameworkUsage usage) : DelegatingChatClient(inner)
    {
        public int CallSequence { get; private set; }
        private readonly System.Text.StringBuilder _lastText = new();
        public string LastText => _lastText.ToString();

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!await isCurrent(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The Ellai session is no longer authorized.");
            var boundedMessages = messages.ToArray();
            if (!usage.CanDispatch(boundedMessages, options))
                throw new InvalidOperationException("The Ellai session budget is exhausted.");
            CallSequence++;
            var response = await base.GetResponseAsync(boundedMessages, options, cancellationToken).ConfigureAwait(false);
            await usage.RecordAsync(response, cancellationToken).ConfigureAwait(false);
            ValidateToolCalls(response.Messages.SelectMany(message => message.Contents), options);
            _lastText.Clear().Append(response.Text);
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!await isCurrent(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The Ellai session is no longer authorized.");
            var boundedMessages = messages.ToArray();
            if (!usage.CanDispatch(boundedMessages, options))
                throw new InvalidOperationException("The Ellai session budget is exhausted.");
            CallSequence++;
            _lastText.Clear();
            UsageDetails? details = null;
            var completed = false;
            try
            {
                await foreach (var update in base.GetStreamingResponseAsync(boundedMessages, options, cancellationToken).ConfigureAwait(false))
                {
                    details = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? details;
                    ValidateToolCalls(update.Contents, options);
                    _lastText.Append(update.Text);
                    yield return update;
                }
                completed = true;
            }
            finally
            {
                if (completed || details is not null)
                    await usage.RecordAsync(new ChatResponse { Usage = details }, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static void ValidateToolCalls(IEnumerable<AIContent> contents, ChatOptions? options)
        {
            var allowed = options?.Tools?.OfType<AIFunctionDeclaration>()
                .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            if (contents.OfType<FunctionCallContent>().Any(call => !string.IsNullOrEmpty(call.Name) && !allowed.Contains(call.Name)))
                throw new InvalidOperationException("The model requested an unavailable tool.");
        }
    }
}
