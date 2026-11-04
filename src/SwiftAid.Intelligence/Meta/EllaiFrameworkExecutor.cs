using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Connects the application utterance boundary to one framework agent run.</summary>
public sealed class EllaiFrameworkExecutor(
    IChatClient client, EllaiPromptBuilder prompts, EllaiToolBroker broker,
    EllaiUsageLedger ledger, IEllaiUsageStore usageStore, TimeSpan? toolTimeout = null,
    IEllaiFrameworkAgentFactory? agentFactory = null, bool enableKnowledgeSpecialist = false,
    bool enableCaseSpecialist = false, bool enableAnalyticsSpecialist = false) : IEllaiReasoningExecutor, IEllaiHistoryAuthorizationRequired
{
    private static readonly System.Text.Json.JsonSerializerOptions CompactTraceJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static EllaiObservedToolResult? PackCaseObservation(EllaiObservedToolResult previous, EllaiObservedToolResult next)
    {
        if (previous.ToolName is not (EllaiToolNames.Topic or EllaiToolNames.Evidence) ||
            !previous.Allowed || !next.Allowed || previous.Truncated || next.Truncated ||
            previous.CaseSnapshotExpiresAtUtc is null || next.CaseSnapshotExpiresAtUtc is null ||
            previous.Evidence.Count == 0 || next.Evidence.Count == 0 ||
            previous.Evidence.Count + next.Evidence.Count > 20 ||
            previous.Evidence.Concat(next.Evidence).Any(item => item.SourceKind != "CaseQueue"))
            return null;

        var previousMetadata = System.Text.Json.JsonSerializer.Serialize(previous with { Evidence = [] }, CompactTraceJson);
        var nextMetadata = System.Text.Json.JsonSerializer.Serialize(next with { Evidence = [] }, CompactTraceJson);
        return string.Equals(previousMetadata, nextMetadata, StringComparison.Ordinal)
            ? previous with { Evidence = previous.Evidence.Concat(next.Evidence).ToArray() }
            : null;
    }
    private int _active;
    private readonly TimeSpan _toolTimeout = toolTimeout is null ? TimeSpan.FromSeconds(30)
        : toolTimeout > TimeSpan.Zero && toolTimeout <= TimeSpan.FromMinutes(5)
            ? toolTimeout.Value : throw new ArgumentOutOfRangeException(nameof(toolTimeout));

    public async Task<EllaiCycleOutcome> RunAsync(EllaiLoopRequest request, CancellationToken ct,
        Func<int, CancellationToken, Task<bool>>? reauthorizeAsync = null,
        EllaiActivityStatusCoordinator? activity = null, EllaiSessionEventJournal? journal = null,
        Func<CancellationToken, Task<bool>>? isExecutionCurrentAsync = null,
        Func<string, CancellationToken, ValueTask>? onTextDelta = null,
        Action<EllaiActivityEvent>? onActivity = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? invokeToolAsync = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return new(false, null, "Another model request is active for this investigation.", 0, 0, false);
        try
        {
            var sequence = 0L;
            var calls = 0;
            var accepted = false;
            var observations = new List<EllaiObservedToolResult>();
            var observationKeys = new HashSet<string>(StringComparer.Ordinal);
            var observationBytes = 0;
            var observationTraceComplete = true;
            void Trace(EllaiActivityKind kind, string detail = "", string? toolName = null)
            {
                var item = new EllaiActivityEvent(request.Context.SessionId, request.ControllerEpoch,
                    Interlocked.Increment(ref sequence), kind, request.CycleId, detail, DateTimeOffset.UtcNow, toolName);
                onActivity?.Invoke(item);
                var status = activity?.Publish(item);
                if (status is not null) journal?.Append(request.Context.SessionId, "activity", status.Phase, status.Text, DateTimeOffset.UtcNow);
            }

            async Task<bool> Current(CancellationToken token) =>
                (reauthorizeAsync is null || await reauthorizeAsync(0, token).ConfigureAwait(false)) &&
                (isExecutionCurrentAsync is null || await isExecutionCurrentAsync(token).ConfigureAwait(false));

            async Task<EllaiToolResult> InvokeCore(EllaiToolContext context, EllaiToolRequest tool, CancellationToken token, bool revalidation)
            {
                if (!revalidation && !accepted)
                {
                    accepted = true;
                    Trace(EllaiActivityKind.OperationAccepted);
                }
                if (!revalidation)
                {
                    Trace(EllaiActivityKind.ToolStarted, "read", tool.ToolName);
                    calls++;
                }
                // This deadline is cooperative: broker/dependency code must observe the token.
                // A noncooperative operation can continue running and keep this await pending.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                var deadlineUtc = DateTimeOffset.UtcNow + _toolTimeout;
                deadline.CancelAfter(_toolTimeout);
                var invocation = (invokeToolAsync ?? broker.InvokeAsync)(
                    context with { ToolDeadlineUtc = deadlineUtc }, tool, deadline.Token);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                try
                {
                    for (var update = 0; update < 2 && !invocation.IsCompleted; update++)
                    {
                        var delay = Task.Delay(TimeSpan.FromSeconds(update == 0 ? 8 : 20), pending.Token);
                        if (await Task.WhenAny(invocation, delay).ConfigureAwait(false) == invocation
                            || invocation.IsCompleted || deadline.IsCancellationRequested) break;
                        if (!revalidation) Trace(EllaiActivityKind.ToolPending, "read", tool.ToolName);
                    }
                    var result = await invocation.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested && !token.IsCancellationRequested)
                        return new(tool.ToolName, false, "Tool execution timed out.", [], string.Empty, 0, false, EllaiToolReasonCodes.Unavailable);
                    if (!revalidation)
                    {
                        if (result.Evidence.Count > 20)
                            observationTraceComplete = false;
                        else if (observationTraceComplete)
                        {
                            var observation = EllaiObservedToolResult.From(tool, result);
                            // Reserve space for the enclosing trace; full serialization
                            // performs the final byte check including inherited history.
                            var key = System.Text.Json.JsonSerializer.Serialize(observation, CompactTraceJson);
                            if (!observationKeys.Contains(key))
                            {
                                var previous = observations.Count == 0 ? null : observations[^1];
                                var packed = previous is null ? null : PackCaseObservation(previous, observation);
                                var bytes = packed is null ? System.Text.Encoding.UTF8.GetByteCount(key) + 1
                                    : System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(packed, CompactTraceJson))
                                        - System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(previous, CompactTraceJson));
                                if (bytes > EllaiToolTrace.MaximumBytes - 512 - observationBytes) observationTraceComplete = false;
                                else
                                {
                                    observationKeys.Add(key);
                                    if (packed is null) observations.Add(observation);
                                    else observations[^1] = packed;
                                    observationBytes += bytes;
                                }
                            }
                        }
                    }
                    if (!revalidation && result.Allowed) Trace(EllaiActivityKind.ToolObservationAccepted);
                    return result;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    return new(tool.ToolName, false, "Tool execution timed out.", [], string.Empty, 0, false, EllaiToolReasonCodes.Unavailable);
                }
                finally { await pending.CancelAsync().ConfigureAwait(false); }
            }

            Task<EllaiToolResult> Invoke(EllaiToolContext context, EllaiToolRequest tool, CancellationToken token) =>
                InvokeCore(context, tool, token, revalidation: false);
            Task<EllaiToolResult> Revalidate(EllaiToolContext context, EllaiToolRequest tool, CancellationToken token) =>
                InvokeCore(context, tool, token, revalidation: true);

            var messages = new List<ChatMessage>();
            foreach (var message in request.Conversation ?? [])
            {
                if (message.Role == "user")
                {
                    messages.Add(new ChatMessage(ChatRole.User, message.Content));
                }
                else if (message.Role == "assistant")
                {
                    var contents = (message.ToolCalls ?? []).Select(call => (AIContent)new FunctionCallContent(
                        call.CallId ?? string.Empty, call.ToolName,
                        call.Arguments.ToDictionary(argument => argument.Key, argument => (object?)argument.Value,
                            StringComparer.Ordinal))).ToList();
                    if (!string.IsNullOrEmpty(message.Content)) contents.Insert(0, new TextContent(message.Content));
                    messages.Add(new ChatMessage(ChatRole.Assistant, contents));
                }
                else if (message.Role == "tool" && !string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(message.ToolCallId, message.Content)]));
                }
            }
            messages.Add(new ChatMessage(ChatRole.User, request.Utterance));
            var agent = new EllaiFrameworkAgent(client, agentFactory);
            SwiftAid.Contracts.Ellai.EllaiDecision? result;
            try
            {
                result = await agent.RunAsync(request.Context,
                    new EllaiExecutionPromptBuilder(prompts, ledger).Build(request),
                    messages, request.MaxTokensPerTurn, Current, Invoke,
                    new EllaiFrameworkUsage(request.Context.SessionId, ledger, usageStore), ct, onTextDelta, Revalidate,
                    request.OnVerifiedStreamSegment,
                    request.RevalidateStreamObservations is null ? null :
                        async token => observationTraceComplete &&
                            await request.RevalidateStreamObservations(observations.ToArray(), token).ConfigureAwait(false),
                    request.FrameworkSessionJson, enableKnowledgeSpecialist: enableKnowledgeSpecialist,
                    enableCaseSpecialist: enableCaseSpecialist, enableAnalyticsSpecialist: enableAnalyticsSpecialist).ConfigureAwait(false);
            }
            catch (EllaiFrameworkFailureException failure) when (failure.Code is not ("ProviderUnavailable" or "UsageUnavailable"))
            {
                Trace(EllaiActivityKind.CycleBlocked, BlockedDetail(failure.Code));
                return new(false, null, "The assistant could not complete this request.", agent.LastModelCallCount, calls, agent.LastRepairUsed, failure.Code);
            }
            if (result is null)
            {
                var code = agent.LastFailureCode ?? "InvalidDecision";
                Trace(EllaiActivityKind.CycleBlocked, BlockedDetail(code));
                return new(false, null, "The assistant could not produce a verified decision.", agent.LastModelCallCount, calls, agent.LastRepairUsed, code);
            }
            if (accepted) Trace(EllaiActivityKind.CycleCompleted, "verified");
            return new(true, result, null, agent.LastModelCallCount, calls, agent.LastRepairUsed)
            {
                ObservedTools = observationTraceComplete ? observations.ToArray() : null,
                FrameworkSessionJson = agent.LastFrameworkSessionJson
            };
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    private static string BlockedDetail(string code) => code switch
    {
        EllaiToolReasonCodes.BudgetExceeded => "budget",
        EllaiToolReasonCodes.ConsentMissing or EllaiToolReasonCodes.DeniedByPolicy or EllaiToolReasonCodes.MissingScope => "denied",
        EllaiToolReasonCodes.Unavailable or "ProviderUnavailable" => "unavailable",
        _ => "invalid"
    };
}
