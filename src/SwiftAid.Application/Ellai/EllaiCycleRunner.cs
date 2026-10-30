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
        Func<int, CancellationToken, Task<bool>>? reauthorizeAsync = null)
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

            for (var iteration = 1; iteration <= MaxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();
                if (reauthorizeAsync is not null && !await reauthorizeAsync(iteration, ct))
                {
                    return new EllaiCycleOutcome(false, null,
                        $"Scope reauthorization failed at iteration {iteration}.", iteration, toolCallsUsed, repairUsed);
                }

                var turn = await modelTurn(iteration);

                foreach (var toolRequest in turn.ToolRequests)
                {
                    if (toolCallsUsed >= MaxToolCalls)
                    {
                        return new EllaiCycleOutcome(false, null,
                            "Model-visible tool budget exhausted.", iteration, toolCallsUsed, repairUsed);
                    }

                    remaining = remaining with { RemainingCalls = MaxToolCalls - toolCallsUsed };
                    var result = await broker.InvokeAsync(remaining, toolRequest, ct);
                    toolCallsUsed++;
                    if (!result.Allowed)
                    {
                        return new EllaiCycleOutcome(false, null,
                            $"Tool {toolRequest.ToolName} denied: {result.DenialReason}", iteration, toolCallsUsed, repairUsed);
                    }
                }

                if (turn.FinalDecision is not null)
                {
                    var errors = validate(turn.FinalDecision);
                    if (errors.Count == 0)
                    {
                        return new EllaiCycleOutcome(true, turn.FinalDecision, null, iteration, toolCallsUsed, repairUsed);
                    }

                    if (repairsAttempted >= MaxRepairAttempts || turn.ToolRequests.Count > 0)
                    {
                        return new EllaiCycleOutcome(false, null,
                            $"Invalid decision: {string.Join("; ", errors)}", iteration, toolCallsUsed, repairUsed);
                    }

                    repairUsed = true;
                    repairsAttempted++;
                }
            }

            return new EllaiCycleOutcome(false, null,
                "Reasoning iteration budget exhausted.", MaxIterations, toolCallsUsed, repairUsed);
        }
        finally
        {
            Interlocked.Exchange(ref _activeRequests, 0);
        }
    }
}
