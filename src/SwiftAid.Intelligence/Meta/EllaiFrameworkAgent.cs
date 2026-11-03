using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>A bounded single-agent execution building block. Gateway integration
/// must supply trusted instructions, durable history, usage and lifecycle fencing.</summary>
public sealed class EllaiFrameworkAgent
{
    private const int MaxResponseCharacters = 512_000;
    private readonly IChatClient _client;
    private readonly IEllaiFrameworkAgentFactory _agentFactory;

    public EllaiFrameworkAgent(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _agentFactory = agentFactory ?? EllaiFrameworkAgentFactory.Instance;
    }
    public int LastModelCallCount { get; private set; }
    public string? LastFailureCode { get; private set; }
    public bool LastRepairUsed { get; private set; }
    public string? LastFrameworkSessionJson { get; private set; }

    public async Task<EllaiDecision?> RunAsync(
        EllaiToolContext context, string instructions, IReadOnlyList<ChatMessage> messages, int maxOutputTokens,
        Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        EllaiFrameworkUsage usage,
        CancellationToken ct, Func<string, CancellationToken, ValueTask>? onTextDelta = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? revalidateTool = null,
        Func<string, CancellationToken, ValueTask>? onVerifiedStreamSegment = null,
        Func<CancellationToken, Task<bool>>? revalidateStreamObservations = null,
        string? frameworkSessionJson = null)
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
        LastFrameworkSessionJson = null;
        LastRepairUsed = false;
        LastModelCallCount = 0;
        async Task<bool> Current(CancellationToken token)
        {
            try
            {
                var allowed = await isCurrent(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return allowed;
            }
            catch (Exception error) when (token.IsCancellationRequested && error is not OperationCanceledException and not OutOfMemoryException)
            {
                throw new OperationCanceledException(token);
            }
        }
        if (context.RemainingCalls <= 0) { LastFailureCode = EllaiToolReasonCodes.BudgetExceeded; return null; }
        var initialCurrent = await Current(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!initialCurrent) { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }

        var evidence = new HashSet<EvidenceRef>();
        var evidenceSources = new Dictionary<EvidenceRef, List<string>>();
        EllaiToolResult? countResult = null;
        EllaiToolRequest? countRequest = null;
        var toolCalls = 0;
        var streamSafeToolObservations = true;
        var guard = new EllaiFrameworkToolGuard(Math.Min(context.RemainingCalls, EllaiExecutionLimits.MaxToolCalls),
            Current, async (toolContext, request, token) =>
            {
                var result = await invoke(toolContext, request, token).ConfigureAwait(false);
                toolCalls++;
                // These brokered observations carry no private evidence and
                // already have explicit safe replay rules in conversation history.
                var publicObservation = request.ToolName is EllaiToolNames.Now or
                    EllaiToolNames.Capabilities or EllaiToolNames.ValidateObservations;
                var emptyCaseRecall = request.ToolName == EllaiToolNames.Topic &&
                    request.Arguments.TryGetValue("ref", out var recallReference) &&
                    string.Equals(recallReference, EllaiCaseResultMemory.Reference, StringComparison.OrdinalIgnoreCase) &&
                    result.HistoryValidUntilUtc > DateTimeOffset.UtcNow && result.ItemCount == 0 &&
                    (result.ResultSummary.TrimEnd() == "The last retained case result set was empty." ||
                     result.ResultSummary.TrimEnd() == "Previously retrieved cases, checked against current access; this is not a new search. Unavailable records are omitted.");
                streamSafeToolObservations &= (publicObservation || emptyCaseRecall) &&
                    result.Allowed && result.ToolName == request.ToolName && !result.Truncated &&
                    result.ReasonCode == EllaiToolReasonCodes.Ok && result.Evidence.Count == 0;
                if (request.ToolName == EllaiToolNames.OwnedCaseCount && result.Allowed)
                {
                    countResult = result;
                    countRequest = request;
                }
                if (result.Allowed)
                    foreach (var reference in result.Evidence.Take(20))
                    {
                        evidence.Add(reference);
                        if (!evidenceSources.TryGetValue(reference, out var sources))
                            evidenceSources[reference] = sources = [];
                        sources.Add(result.ResultSummary);
                    }
                return result;
            });
        async Task<EllaiToolResult> InvokeGuardedTool(EllaiToolContext toolContext, EllaiToolRequest request, CancellationToken token)
        {
            var result = await guard.InvokeAsync(toolContext, request, token).ConfigureAwait(false);
            // Only stop after the guard's post-call authorization has passed.
            // The final count is constructed from a fresh authorized read below.
            if (request.ToolName == EllaiToolNames.OwnedCaseCount && result.Allowed && toolCalls == 1 &&
                FunctionInvokingChatClient.CurrentContext is { FunctionCount: 1 } invocation)
                invocation.Terminate = true;
            return result;
        }
        var tools = EllaiFrameworkToolCatalog.Create(context, InvokeGuardedTool);
        var guardedClient = new ModelCallGuard(_client, Current, usage);
        var functionClient = new FunctionInvokingChatClient(guardedClient)
        {
            MaximumIterationsPerRequest = EllaiExecutionLimits.MaxIterations
        };
        if (onVerifiedStreamSegment is not null)
            instructions += "\nFor a plain Answer with no UI action or question, put stream_segments first in the final JSON envelope. " +
                "Each entry is an object with text and claims, where claims use the same GroundedClaim schema and exact evidence as final_decision. " +
                "Each text is a complete independently supported sentence. The final_decision displayText and spokenText must both equal " +
                "the segment texts joined by one space. For other decision kinds omit stream_segments. " +
                "Example shape: {\"stream_segments\":[{\"text\":\"Hello.\",\"claims\":[]}],\"final_decision\":{...}}.";
        AIAgent agent = _agentFactory.Create(functionClient, new ChatClientAgentOptions
        {
            Name = "Ellai",
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools.ToList(), MaxOutputTokens = maxOutputTokens }
        });
        var history = messages.Take(messages.Count - 1).ToList();
        var owner = context.Actor?.ActorId;
        var restore = frameworkSessionJson is not null && owner is not null &&
            EllaiFrameworkSessionCodec.IsCurrentScope(frameworkSessionJson, context.SessionId, owner, context.ScopeEpoch);
        var session = restore
            ? await EllaiFrameworkSessionCodec.RestoreAsync(agent, frameworkSessionJson!, context.SessionId,
                owner!, context.ScopeEpoch, history, ct).ConfigureAwait(false)
            : await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
        if (!restore) session.SetInMemoryChatHistory(history);
        async Task SaveSessionAsync()
        {
            if (owner is null) return;
            // Conversation is reauthorized from the journal at the next turn.
            // Do not persist provider tool messages or model-generated history.
            session.SetInMemoryChatHistory([]);
            LastFrameworkSessionJson = await EllaiFrameworkSessionCodec.SerializeAsync(
                agent, session, context.SessionId, owner, context.ScopeEpoch, ct).ConfigureAwait(false);
        }
        var currentMessage = messages[^1];
        var streamedSegments = new List<string>();
        var streamingSuppressed = false;
        try
        {
            if (onTextDelta is null && onVerifiedStreamSegment is null)
            {
                await agent.RunAsync(currentMessage, session, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                // Only complete segments with independently valid claims and
                // current authority may leave the streaming model response.
                var textLength = 0;
                var parser = new EllaiVerifiedSegmentParser();
                var modelCall = 0;
                await foreach (var update in agent.RunStreamingAsync(currentMessage, session, cancellationToken: ct).ConfigureAwait(false))
                {
                    if (update.Text.Length > MaxResponseCharacters - textLength)
                        throw new EllaiFrameworkFailureException("ResponseTooLarge");
                    textLength += update.Text.Length;
                    if (modelCall != guardedClient.CallSequence)
                    {
                        if (streamedSegments.Count != 0) throw new EllaiFrameworkFailureException("InvalidDecision");
                        modelCall = guardedClient.CallSequence;
                        parser = new EllaiVerifiedSegmentParser();
                    }
                    foreach (var parsedSegment in parser.Append(update.Text, evidence))
                    {
                        var segment = parsedSegment.Text;
                        // Private tool evidence may have lost authority since
                        // the tool call. Keep it buffered until the final fence.
                        // Owned-case counts use a server-built decision after a
                        // fresh aggregate read, so model text is not publishable.
                        if (onVerifiedStreamSegment is null || countResult is not null || streamingSuppressed) continue;
                        // Evidence-backed prose may be released early only when
                        // it matches a complete line of every cited observation.
                        // Paraphrases still flow through the committed answer.
                        if (evidence.Count != 0 && parsedSegment.Claims.Any(claim =>
                            claim.Evidence.Any(reference => !evidenceSources.TryGetValue(reference, out var sources) ||
                                !sources.Any(source => source.Split('\n').Any(line =>
                                    string.Equals(line.Trim(), segment, StringComparison.Ordinal))))))
                        {
                            if (streamedSegments.Count != 0) throw new EllaiFrameworkFailureException("InvalidDecision");
                            streamingSuppressed = true;
                            continue;
                        }
                        ct.ThrowIfCancellationRequested();
                        if (guard.FailureCode is not null || !await Current(ct).ConfigureAwait(false))
                            throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
                        if (!streamSafeToolObservations && (revalidateStreamObservations is null ||
                            !await revalidateStreamObservations(ct).ConfigureAwait(false)))
                        {
                            throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
                        }
                        if (!await Current(ct).ConfigureAwait(false))
                            throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
                        if (string.Join(" ", streamedSegments.Append(segment)).Length > EllaiDecisionValidator.MaxDisplayChars)
                            throw new EllaiFrameworkFailureException("ResponseTooLarge");
                        await onVerifiedStreamSegment(segment, ct).ConfigureAwait(false);
                        streamedSegments.Add(segment);
                    }
                }
            }
        }
        finally { LastModelCallCount = guardedClient.CallSequence; }
        EllaiDecision? decision = null;
        if (guard.FailureCode is not null) { LastFailureCode = guard.FailureCode; return null; }
        while (countResult is null)
        {
            LastModelCallCount = guardedClient.CallSequence;
            if (guard.FailureCode is not null) { LastFailureCode = guard.FailureCode; return null; }
            if (!await Current(ct).ConfigureAwait(false)) { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
            decision = EllaiModelTurnParser.Parse(guardedClient.LastText).Turn?.FinalDecision;
            if (decision is not null && EllaiDecisionValidator.Validate(decision).Count == 0 &&
                !decision.Claims.Any(claim => claim?.Evidence is { } refs && refs.Any(reference => reference is null || !evidence.Contains(reference))))
                break;
            decision = null;
            // Published speech cannot be retracted or replaced by a repaired
            // answer. End this turn rather than generating a different one.
            if (streamedSegments.Count != 0) { LastFailureCode = "InvalidDecision"; return null; }
            // A correction is a model-only framework run in the same session;
            // no tool or new data access is granted during output repair.
            guardedClient.RepairMode = true;
            LastRepairUsed = true;
            try
            {
                await agent.RunAsync("The previous decision was invalid. Return only a valid final_decision JSON object using the required schema and observed evidence.",
                    session, cancellationToken: ct).ConfigureAwait(false);
            }
            finally { LastModelCallCount = guardedClient.CallSequence; }
        }
        if (countResult is not null)
        {
            if (streamedSegments.Count != 0) { LastFailureCode = "InvalidDecision"; return null; }
            if (toolCalls != 1 || !HasCountEvidence(countResult) || countRequest is null ||
                countResult.CountReplayScope is not { } originalScope || !originalScope.IsValid() ||
                !countRequest.Arguments.TryGetValue("window_days", out var requestedWindow) ||
                !int.TryParse(requestedWindow, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var windowDays) || windowDays != originalScope.WindowDays ||
                !await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            // Re-read through the same scoped, authorized broker immediately before
            // publication; the session fence alone does not detect grant changes.
            var fresh = await (revalidateTool ?? invoke)(context, countRequest, ct).ConfigureAwait(false);
            if (!fresh.Allowed || !HasCountEvidence(fresh) ||
                fresh.CountReplayScope != originalScope ||
                !TryCount(countResult.ResultSummary, out var previousCount) ||
                !TryCount(fresh.ResultSummary, out var freshCount) || previousCount != freshCount ||
                !await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            countResult = fresh;
            decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.Answer,
                countResult.ResultSummary, countResult.ResultSummary,
                [new GroundedClaim("owned-case-count", countResult.ResultSummary, ClaimKind.Observed, countResult.Evidence)],
                [], [], null, [], null, null);
            if (EllaiDecisionValidator.Validate(decision).Count != 0)
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            if (!await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            if (onTextDelta is not null && !await PublishValidatedTextAsync(decision.DisplayText, onTextDelta, Current, ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            if (!await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            await SaveSessionAsync().ConfigureAwait(false);
            return decision;
        }
        if (decision is null) return null;
        if (!streamSafeToolObservations && revalidateStreamObservations is not null &&
            !await revalidateStreamObservations(ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        if (streamedSegments.Count != 0 &&
            (decision.Kind != DecisionKind.Answer ||
             !string.Equals(string.Join(" ", streamedSegments), decision.DisplayText.Trim(), StringComparison.Ordinal) ||
             !string.Equals(string.Join(" ", streamedSegments), decision.SpokenText?.Trim(), StringComparison.Ordinal)))
        {
            LastFailureCode = "InvalidDecision";
            return null;
        }
        if (!await Current(ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        if (onTextDelta is not null && streamedSegments.Count == 0 && !await PublishValidatedTextAsync(decision.DisplayText, onTextDelta, Current, ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        if (!await Current(ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        await SaveSessionAsync().ConfigureAwait(false);
        return decision;
    }

    private static async Task<bool> PublishValidatedTextAsync(string text,
        Func<string, CancellationToken, ValueTask> publish, Func<CancellationToken, Task<bool>> isCurrent,
        CancellationToken ct)
    {
        // The response feed trims each chunk and joins chunks with one space.
        // Split only on a single ordinary space so that joining preserves text.
        var start = 0;
        for (var index = 0; index < text.Length - 2; index++)
        {
            if (text[index] is not ('.' or '?' or '!') || text[index + 1] != ' ' ||
                char.IsWhiteSpace(text[index + 2])) continue;
            ct.ThrowIfCancellationRequested();
            if (!await isCurrent(ct).ConfigureAwait(false)) return false;
            await publish(text[start..(index + 1)], ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            start = index + 2;
        }
        ct.ThrowIfCancellationRequested();
        if (!await isCurrent(ct).ConfigureAwait(false)) return false;
        await publish(text[start..], ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return true;
    }

    private static bool TryCount(string summary, out int count)
    {
        const string marker = " managed case(s) ";
        var end = summary.IndexOf(marker, StringComparison.Ordinal);
        count = 0;
        return end > 0 && int.TryParse(summary.AsSpan(0, end), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out count) && count >= 0;
    }

    private static bool HasCountEvidence(EllaiToolResult result) => result.Evidence.Count == 1 &&
        result.Evidence[0] is { SourceKind: "DashboardAggregate", ResourceId: "owned-cases" };

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
            if (RepairMode)
            {
                options = options?.Clone() ?? new ChatOptions();
                options.Tools = [];
                options.ToolMode = ChatToolMode.None;
            }
            var boundedMessages = messages.ToArray();
            for (var attempt = 0; ; attempt++)
            {
                var current = await isCurrent(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!current) throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
                using var reservation = usage.TryBeginModelCall(boundedMessages, options);
                if (reservation is null)
                    throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.BudgetExceeded);
                cancellationToken.ThrowIfCancellationRequested();
                CallSequence++;
                ChatResponse? response = null;
                try { response = await base.GetResponseAsync(boundedMessages, options, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (cancellationToken.IsCancellationRequested && error is not OutOfMemoryException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (System.ClientModel.ClientResultException error) { throw ProviderFailure(error); }
                catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
                finally
                {
                    // Each dispatch, including a truncated one, may be billable.
                    await usage.RecordAsync(response ?? new ChatResponse(), boundedMessages, options, CancellationToken.None).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (IsOutputLimit(response.FinishReason))
                {
                    // The framework has not seen this response, so any incomplete
                    // function calls in it cannot have executed. Retry only once.
                    if (attempt != 0 || options?.MaxOutputTokens is not > 0 or >= 16384)
                        throw new EllaiFrameworkFailureException("ProviderOutputLimit");
                    options = options.Clone();
                    options.MaxOutputTokens = Math.Min(options.MaxOutputTokens!.Value * 2, 16384);
                    continue;
                }
                ValidateToolCalls(response.Messages.SelectMany(message => message.Contents), options);
                var responseText = response.Text;
                if (responseText.Length > MaxResponseCharacters)
                    throw new EllaiFrameworkFailureException("ResponseTooLarge");
                _lastText.Clear().Append(responseText);
                return response;
            }
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var current = await isCurrent(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!current) throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
            var boundedMessages = messages.ToArray();
            using var reservation = usage.TryBeginModelCall(boundedMessages, options);
            if (reservation is null)
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.BudgetExceeded);
            cancellationToken.ThrowIfCancellationRequested();
            CallSequence++;
            _lastText.Clear();
            UsageDetails? details = null;
            var outputLimitReached = false;
            try
            {
                await using var stream = CreateStreamSafe(boundedMessages, options, cancellationToken);
                while (await MoveNextSafeAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    // A provider may return an update after ignoring cancellation.
                    // Never pass that late update to framework tool invocation.
                    cancellationToken.ThrowIfCancellationRequested();
                    var update = stream.Current;
                    outputLimitReached |= IsOutputLimit(update.FinishReason);
                    foreach (var reported in update.Contents.OfType<UsageContent>().Select(content => content.Details))
                    {
                        if (reported is null) continue;
                        details ??= new UsageDetails();
                        details.InputTokenCount = reported.InputTokenCount ?? details.InputTokenCount;
                        details.OutputTokenCount = reported.OutputTokenCount ?? details.OutputTokenCount;
                    }
                    ValidateToolCalls(update.Contents, options);
                    var text = update.Text;
                    if (text.Length > MaxResponseCharacters - _lastText.Length)
                        throw new EllaiFrameworkFailureException("ResponseTooLarge");
                    _lastText.Append(text);
                    yield return update;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (outputLimitReached)
                    throw new EllaiFrameworkFailureException("ProviderOutputLimit");
            }
            finally
            {
                await usage.RecordAsync(new ChatResponse { Usage = details }, boundedMessages, options, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static void ValidateToolCalls(IEnumerable<AIContent> contents, ChatOptions? options)
        {
            var allowed = options?.Tools?.OfType<AIFunctionDeclaration>()
                .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            if (contents.OfType<FunctionCallContent>().Any(call => string.IsNullOrWhiteSpace(call.Name) || !allowed.Contains(call.Name)))
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.UnregisteredTool);
        }

        private static async ValueTask<bool> MoveNextSafeAsync(IAsyncEnumerator<ChatResponseUpdate> stream, CancellationToken ct)
        {
            try { return await stream.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (ct.IsCancellationRequested && error is not OutOfMemoryException)
            {
                throw new OperationCanceledException(ct);
            }
            catch (System.ClientModel.ClientResultException error) { throw ProviderFailure(error); }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
        }

        private IAsyncEnumerator<ChatResponseUpdate> CreateStreamSafe(IReadOnlyList<ChatMessage> messages,
            ChatOptions? options, CancellationToken ct)
        {
            try { return new SafeDisposingEnumerator(base.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (ct.IsCancellationRequested && error is not OutOfMemoryException)
            {
                throw new OperationCanceledException(ct);
            }
            catch (System.ClientModel.ClientResultException error) { throw ProviderFailure(error); }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
        }

        private sealed class SafeDisposingEnumerator(IAsyncEnumerator<ChatResponseUpdate> inner, CancellationToken ct)
            : IAsyncEnumerator<ChatResponseUpdate>
        {
            public ChatResponseUpdate Current => inner.Current;
            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();

            public async ValueTask DisposeAsync()
            {
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (ct.IsCancellationRequested && error is not OutOfMemoryException)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    throw new EllaiFrameworkFailureException("ProviderUnavailable");
                }
            }
        }

        private static EllaiFrameworkFailureException ProviderFailure(System.ClientModel.ClientResultException error)
            => new(error.Status switch
            {
                401 or 403 => "ProviderAuthenticationFailed",
                413 => "ProviderRequestTooLarge",
                429 => "ProviderRateLimited",
                _ => "ProviderUnavailable"
            });

        private static bool IsOutputLimit(object? finishReason)
            => string.Equals(finishReason?.ToString(), "Length", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Creates native Agent Framework agents from each run's guarded client and options.</summary>
public interface IEllaiFrameworkAgentFactory
{
    AIAgent Create(IChatClient client, ChatClientAgentOptions options);
}

/// <summary>Default native AIAgent composition used by the DI-owned Ellai factory.</summary>
public sealed class EllaiFrameworkAgentFactory : IEllaiFrameworkAgentFactory
{
    public static EllaiFrameworkAgentFactory Instance { get; } = new();

    public AIAgent Create(IChatClient client, ChatClientAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        return client.AsAIAgent(options);
    }
}

/// <summary>Safe, non-provider diagnostic for an unsuccessful framework call.</summary>
public sealed class EllaiFrameworkFailureException(string code) : InvalidOperationException("Ellai framework execution was stopped.")
{
    public string Code { get; } = code;
}
