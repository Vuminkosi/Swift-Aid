using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Connects the application utterance boundary to one framework agent run.</summary>
public sealed class EllaiFrameworkExecutor(
    IChatClient client, EllaiPromptBuilder prompts, EllaiToolBroker broker,
    EllaiUsageLedger ledger, IEllaiUsageStore usageStore, TimeSpan? toolTimeout = null) : IEllaiReasoningExecutor
{
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
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(_toolTimeout);
                var invocation = (invokeToolAsync ?? broker.InvokeAsync)(context, tool, deadline.Token);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
                try
                {
                    for (var update = 0; update < 2 && !invocation.IsCompleted; update++)
                    {
                        var delay = Task.Delay(TimeSpan.FromSeconds(update == 0 ? 8 : 20), pending.Token);
                        if (await Task.WhenAny(invocation, delay).ConfigureAwait(false) == invocation
                            || invocation.IsCompleted || token.IsCancellationRequested) break;
                        if (!revalidation) Trace(EllaiActivityKind.ToolPending, "read", tool.ToolName);
                    }
                    var result = await invocation.ConfigureAwait(false);
                    if (deadline.IsCancellationRequested && !token.IsCancellationRequested)
                        return new(tool.ToolName, false, "Tool execution timed out.", [], string.Empty, 0, false, EllaiToolReasonCodes.Unavailable);
                    if (!revalidation)
                    {
                        if (observations.Count >= 256 || result.Evidence.Count > 20)
                            observationTraceComplete = false;
                        else
                            observations.Add(new(tool.ToolName, result.Allowed, result.ReasonCode,
                                result.Evidence.ToArray(), result.Truncated));
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

            var messages = (request.Conversation ?? []).Where(message => message.Role is "user" or "assistant")
                .Select(message => new ChatMessage(message.Role == "user" ? ChatRole.User : ChatRole.Assistant, message.Content))
                .ToList();
            messages.Add(new ChatMessage(ChatRole.User, request.Utterance));
            var agent = new EllaiFrameworkAgent(client);
            SwiftAid.Contracts.Ellai.EllaiDecision? result;
            try
            {
                result = await agent.RunAsync(request.Context,
                    new EllaiExecutionPromptBuilder(prompts, ledger).Build(request, frameworkNative: true),
                    messages, request.MaxTokensPerTurn, Current, Invoke,
                    new EllaiFrameworkUsage(request.Context.SessionId, ledger, usageStore), ct, onTextDelta, Revalidate).ConfigureAwait(false);
            }
            catch (EllaiFrameworkFailureException failure) when (failure.Code != "ProviderUnavailable")
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
                ObservedTools = observationTraceComplete ? observations.ToArray() : null
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
