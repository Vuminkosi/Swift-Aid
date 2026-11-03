using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Connects the application utterance boundary to one framework agent run.</summary>
public sealed class EllaiFrameworkExecutor(
    IChatClient client, EllaiPromptBuilder prompts, EllaiToolBroker broker,
    EllaiUsageLedger ledger, IEllaiUsageStore usageStore) : IEllaiReasoningExecutor
{
    private int _active;

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

            async Task<EllaiToolResult> Invoke(EllaiToolContext context, EllaiToolRequest tool, CancellationToken token)
            {
                if (!accepted)
                {
                    accepted = true;
                    Trace(EllaiActivityKind.OperationAccepted);
                }
                Trace(EllaiActivityKind.ToolStarted, "read", tool.ToolName);
                calls++;
                var result = await (invokeToolAsync ?? broker.InvokeAsync)(context, tool, token).ConfigureAwait(false);
                if (result.Allowed) Trace(EllaiActivityKind.ToolObservationAccepted);
                return result;
            }

            var messages = (request.Conversation ?? []).Where(message => message.Role is "user" or "assistant")
                .Select(message => new ChatMessage(message.Role == "user" ? ChatRole.User : ChatRole.Assistant, message.Content))
                .ToList();
            messages.Add(new ChatMessage(ChatRole.User, request.Utterance));
            var agent = new EllaiFrameworkAgent(client);
            var result = await agent.RunAsync(request.Context,
                new EllaiExecutionPromptBuilder(prompts, ledger).Build(request, frameworkNative: true),
                messages, request.MaxTokensPerTurn, Current, Invoke,
                new EllaiFrameworkUsage(request.Context.SessionId, ledger, usageStore), ct, onTextDelta).ConfigureAwait(false);
            if (result is null)
            {
                Trace(EllaiActivityKind.CycleBlocked, "invalid");
                return new(false, null, "The assistant could not produce a verified decision.", agent.LastModelCallCount, calls, false, "InvalidDecision");
            }
            if (accepted) Trace(EllaiActivityKind.CycleCompleted, "verified");
            return new(true, result, null, agent.LastModelCallCount, calls, false);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }
}
