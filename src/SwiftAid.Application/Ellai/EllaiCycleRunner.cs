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

/// <summary>Bounded multi-turn cycle runner (ELL-T06 groundwork). Hard limits:
/// 3 reasoning iterations, 4 model-visible tool calls, 1 model-only schema
/// repair, 12 session cycles. Counters never reset on role change, provider
/// fallback or rephrased goals; one active request per investigation.</summary>
public sealed class EllaiCycleRunner(EllaiToolBroker broker)
{
    public const int MaxIterations = 3;

    public const int MaxToolCalls = 4;

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

                    if (repairUsed || turn.ToolRequests.Count > 0)
                    {
                        return new EllaiCycleOutcome(false, null,
                            $"Invalid decision: {string.Join("; ", errors)}", iteration, toolCallsUsed, repairUsed);
                    }

                    repairUsed = true;
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
