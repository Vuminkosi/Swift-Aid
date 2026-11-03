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
    public string? LastFailureCode { get; private set; }
    public bool LastRepairUsed { get; private set; }

    public async Task<EllaiDecision?> RunAsync(
        EllaiToolContext context, string instructions, IReadOnlyList<ChatMessage> messages, int maxOutputTokens,
        Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        EllaiFrameworkUsage usage,
        CancellationToken ct, Func<string, CancellationToken, ValueTask>? onTextDelta = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? revalidateTool = null)
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
        LastFailureCode = null;
        LastRepairUsed = false;
        LastModelCallCount = 0;
        if (context.RemainingCalls <= 0) { LastFailureCode = EllaiToolReasonCodes.BudgetExceeded; return null; }
        if (!await isCurrent(ct).ConfigureAwait(false)) { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }

        var evidence = new HashSet<EvidenceRef>();
        EllaiToolResult? countResult = null;
        EllaiToolRequest? countRequest = null;
        var toolCalls = 0;
        var bufferForCount = context.AllowedTools is null || context.AllowedTools.Contains(EllaiToolNames.OwnedCaseCount);
        var guard = new EllaiFrameworkToolGuard(Math.Min(context.RemainingCalls, EllaiCycleRunner.MaxToolCalls),
            isCurrent, async (toolContext, request, token) =>
            {
                var result = await invoke(toolContext, request, token).ConfigureAwait(false);
                toolCalls++;
                if (request.ToolName == EllaiToolNames.OwnedCaseCount && result.Allowed)
                {
                    countResult = result;
                    countRequest = request;
                }
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
        var emittedProvisional = false;
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
                    if (!bufferForCount && guard.FailureCode is null && await isCurrent(ct).ConfigureAwait(false))
                    {
                        await onTextDelta(sentence, ct).ConfigureAwait(false);
                        emittedProvisional = true;
                    }
                }
            }
        }
        EllaiDecision? decision = null;
        for (var repair = 0; repair <= EllaiCycleRunner.MaxRepairAttempts; repair++)
        {
            LastModelCallCount = guardedClient.CallSequence;
            if (guard.FailureCode is not null) { LastFailureCode = guard.FailureCode; return null; }
            if (!await isCurrent(ct).ConfigureAwait(false)) { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
            decision = EllaiModelTurnParser.Parse(guardedClient.LastText).Turn?.FinalDecision;
            if (decision is not null && EllaiDecisionValidator.Validate(decision).Count == 0 &&
                !decision.Claims.Any(claim => claim?.Evidence is { } refs && refs.Any(reference => reference is null || !evidence.Contains(reference))))
                break;
            decision = null;
            if (repair == EllaiCycleRunner.MaxRepairAttempts || guardedClient.CallSequence >= EllaiCycleRunner.MaxIterations) return null;

            // A correction is a model-only framework run in the same session;
            // no tool or new data access is granted during output repair.
            guardedClient.RepairMode = true;
            LastRepairUsed = true;
            await agent.RunAsync("The previous decision was invalid. Return only a valid final_decision JSON object using the required schema and observed evidence.",
                session, cancellationToken: ct).ConfigureAwait(false);
            parser = null;
        }
        if (decision is null) return null;
        if (countResult is not null)
        {
            if (toolCalls != 1 || countResult.Evidence.Count != 1 || !await isCurrent(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            // Re-read through the same scoped, authorized broker immediately before
            // publication; the session fence alone does not detect grant changes.
            var fresh = await (revalidateTool ?? invoke)(context, countRequest!, ct).ConfigureAwait(false);
            if (!fresh.Allowed || fresh.Evidence.Count != 1 ||
                !TryCount(countResult.ResultSummary, out var previousCount) ||
                !TryCount(fresh.ResultSummary, out var freshCount) || previousCount != freshCount ||
                !await isCurrent(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            countResult = fresh;
            decision = decision with
            {
                Kind = DecisionKind.Answer,
                DisplayText = countResult.ResultSummary,
                SpokenText = countResult.ResultSummary,
                Claims = [new GroundedClaim("owned-case-count", countResult.ResultSummary, ClaimKind.Observed, countResult.Evidence)],
                Questions = [], UiIntents = [], PlanProposal = null, TopicRefs = [], ResumeSuggestion = null, StopReason = null
            };
            if (!await isCurrent(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            if (onTextDelta is not null)
                await onTextDelta(decision.DisplayText, ct).ConfigureAwait(false);
            if (!await isCurrent(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            return decision;
        }
        if (!await isCurrent(ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        if (bufferForCount && onTextDelta is not null)
        {
            await onTextDelta(decision.DisplayText, ct).ConfigureAwait(false);
        }
        else if (parser is not null && onTextDelta is not null)
        {
            var remainder = parser.FlushVerified(decision.DisplayText);
            if (remainder.Length > 0 && await isCurrent(ct).ConfigureAwait(false))
                await onTextDelta(remainder, ct).ConfigureAwait(false);
        }
        else if (!emittedProvisional && onTextDelta is not null && await isCurrent(ct).ConfigureAwait(false))
            await onTextDelta(decision.DisplayText, ct).ConfigureAwait(false);
        if (!await isCurrent(ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        return decision;
    }

    private static bool TryCount(string summary, out int count)
    {
        const string marker = " managed case(s) ";
        var end = summary.IndexOf(marker, StringComparison.Ordinal);
        count = 0;
        return end > 0 && int.TryParse(summary.AsSpan(0, end), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out count) && count >= 0;
    }

    private sealed class ModelCallGuard(IChatClient inner, Func<CancellationToken, Task<bool>> isCurrent,
        EllaiFrameworkUsage usage) : DelegatingChatClient(inner)
    {
        public int CallSequence { get; private set; }
        public bool RepairMode { get; set; }
        private readonly System.Text.StringBuilder _lastText = new();
        public string LastText => _lastText.ToString();

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!await isCurrent(cancellationToken).ConfigureAwait(false))
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
            if (CallSequence >= EllaiCycleRunner.MaxIterations)
                throw new EllaiFrameworkFailureException("IterationLimit");
            if (RepairMode)
            {
                options = options?.Clone() ?? new ChatOptions();
                options.Tools = [];
                options.ToolMode = ChatToolMode.None;
            }
            var boundedMessages = messages.ToArray();
            if (!usage.CanDispatch(boundedMessages, options))
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.BudgetExceeded);
            CallSequence++;
            ChatResponse response;
            try { response = await base.GetResponseAsync(boundedMessages, options, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
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
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
            if (CallSequence >= EllaiCycleRunner.MaxIterations)
                throw new EllaiFrameworkFailureException("IterationLimit");
            var boundedMessages = messages.ToArray();
            if (!usage.CanDispatch(boundedMessages, options))
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.BudgetExceeded);
            CallSequence++;
            _lastText.Clear();
            UsageDetails? details = null;
            var completed = false;
            try
            {
                await using var stream = CreateStreamSafe(boundedMessages, options, cancellationToken);
                while (await MoveNextSafeAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    var update = stream.Current;
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
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.UnregisteredTool);
        }

        private static async ValueTask<bool> MoveNextSafeAsync(IAsyncEnumerator<ChatResponseUpdate> stream, CancellationToken ct)
        {
            try { return await stream.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
        }

        private IAsyncEnumerator<ChatResponseUpdate> CreateStreamSafe(IReadOnlyList<ChatMessage> messages,
            ChatOptions? options, CancellationToken ct)
        {
            try { return base.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
        }
    }
}

/// <summary>Safe, non-provider diagnostic for an unsuccessful framework call.</summary>
public sealed class EllaiFrameworkFailureException(string code) : InvalidOperationException("Ellai framework execution was stopped.")
{
    public string Code { get; } = code;
}
