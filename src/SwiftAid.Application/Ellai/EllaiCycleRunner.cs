using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Outcome of one bounded Ellai cycle.</summary>
public sealed record EllaiCycleOutcome(
    bool Completed,
    EllaiDecision? Decision,
    string? StopReason,
    int IterationsUsed,
    int ToolCallsUsed,
    bool RepairUsed);

/// <summary>Model-turn delegate: one bounded reasoning step returning either
/// tool requests or a final decision draft.</summary>
public sealed record EllaiModelTurn(
    IReadOnlyList<EllaiToolRequest> ToolRequests,
    EllaiDecision? FinalDecision);

/// <summary>Generous multi-turn cycle runner (ELL-T06 groundwork). Safety
/// ceilings sit far above normal use — 25 reasoning iterations, 60
/// model-visible tool calls, 5 model-only schema repairs, 100 session
/// cycles — so legitimate work never hits a quota wall; the $70/token
/// budgets in <see cref="EllaiUsageLedger"/> are the real governors.
/// Counters never reset on role change, provider fallback or rephrased
/// goals; one active request per investigation (concurrency safety, not
/// a quota).</summary>
public sealed class EllaiCycleRunner(EllaiToolBroker broker)
{
    public const int MaxIterations = 25;

    public const int MaxToolCalls = 60;

    public const int MaxRepairAttempts = 5;

    private int _activeRequests;

    public async Task<EllaiCycleOutcome> RunAsync(
        EllaiToolContext context,
        Func<int, Task<EllaiModelTurn>> modelTurn,
        Func<EllaiDecision, IReadOnlyList<string>> validate,
        CancellationToken ct,
        Func<int, CancellationToken, Task<bool>>? reauthorizeAsync = null,
        EllaiActivityStatusCoordinator? activity = null,
        string cycleId = "cycle-1",
        long controllerEpoch = 1)
    {
        ArgumentNullException.ThrowIfNull(modelTurn);
        ArgumentNullException.ThrowIfNull(validate);

        if (Interlocked.CompareExchange(ref _activeRequests, 1, 0) != 0)
        {
            return new EllaiCycleOutcome(false, null, "Another model request is active for this investigation.", 0, 0, false);
        }

        try
        {
            var toolCallsUsed = 0;
            var repairUsed = false;
            var repairsAttempted = 0;
            var remaining = context;
            long sequence = 0;
            var now = DateTimeOffset.UtcNow;

            void Trace(EllaiActivityKind kind, string detail = "")
            {
                if (activity is not null)
                {
                    activity.Publish(new EllaiActivityEvent(
                        context.SessionId, controllerEpoch, ++sequence, kind, cycleId, detail, now));
                }
            }

            EllaiCycleOutcome Stop(string reason, int iteration, string blockedDetail)
            {
                Trace(EllaiActivityKind.CycleBlocked, blockedDetail);
                return new EllaiCycleOutcome(false, null, reason, iteration, toolCallsUsed, repairUsed);
            }

            Trace(EllaiActivityKind.OperationAccepted);

            for (var iteration = 1; iteration <= MaxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();
                if (reauthorizeAsync is not null && !await reauthorizeAsync(iteration, ct))
                {
                    return Stop($"Scope reauthorization failed at iteration {iteration}.", iteration, "denied");
                }

                Trace(EllaiActivityKind.ReasoningTurnStarted);
                var turn = await modelTurn(iteration);

                foreach (var toolRequest in turn.ToolRequests)
                {
                    if (toolCallsUsed >= MaxToolCalls)
                    {
                        return Stop("Model-visible tool budget exhausted.", iteration, "budget");
                    }

                    Trace(EllaiActivityKind.ToolStarted, ActivityDetail(toolRequest.ToolName));
                    remaining = remaining with { RemainingCalls = MaxToolCalls - toolCallsUsed };
                    var result = await broker.InvokeAsync(remaining, toolRequest, ct);
                    toolCallsUsed++;
                    if (!result.Allowed)
                    {
                        return Stop($"Tool {toolRequest.ToolName} denied: {result.DenialReason}",
                            iteration, BlockedDetail(result.ReasonCode));
                    }

                    Trace(EllaiActivityKind.ToolObservationAccepted);
                }

                if (turn.FinalDecision is not null)
                {
                    var errors = validate(turn.FinalDecision);
                    if (errors.Count == 0)
                    {
                        Trace(EllaiActivityKind.CycleCompleted, "verified");
                        return new EllaiCycleOutcome(true, turn.FinalDecision, null, iteration, toolCallsUsed, repairUsed);
                    }

                    if (repairsAttempted >= MaxRepairAttempts || turn.ToolRequests.Count > 0)
                    {
                        return Stop($"Invalid decision: {string.Join("; ", errors)}", iteration, string.Empty);
                    }

                    repairUsed = true;
                    repairsAttempted++;
                }
            }

            return Stop("Reasoning iteration budget exhausted.", MaxIterations, "budget");
        }
        finally
        {
            Interlocked.Exchange(ref _activeRequests, 0);
        }
    }

    private static string ActivityDetail(string toolName) =>
        string.Equals(toolName, EllaiToolNames.DocsSearch, StringComparison.Ordinal)
        || string.Equals(toolName, EllaiToolNames.DocsGet, StringComparison.Ordinal)
            ? "docs"
            : string.Equals(toolName, EllaiToolNames.Receipts, StringComparison.Ordinal)
                ? "receipt"
                : "read";

    private static string BlockedDetail(string reasonCode) =>
        reasonCode switch
        {
            EllaiToolReasonCodes.BudgetExceeded => "budget",
            EllaiToolReasonCodes.Unavailable => "unavailable",
            EllaiToolReasonCodes.ConsentMissing => "denied",
            EllaiToolReasonCodes.DeniedByPolicy => "denied",
            EllaiToolReasonCodes.MissingScope => "denied",
            _ => string.Empty,
        };
}
