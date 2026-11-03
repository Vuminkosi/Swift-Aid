using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Outcome of one bounded Ellai cycle.</summary>
public sealed record EllaiCycleOutcome(
    bool Completed,
    EllaiDecision? Decision,
    string? StopReason,
    int IterationsUsed,
    int ToolCallsUsed,
    bool RepairUsed,
    string? FailureCode = null)
{
    /// <summary>Server-observed tool results for this execution. Null means no
    /// complete observation trace is available; it is never authorization.</summary>
    public IReadOnlyList<EllaiObservedToolResult>? ObservedTools { get; init; }
}

/// <summary>Bounded tool provenance, excluding arguments and result text.</summary>
public sealed record EllaiObservedToolResult(
    string ToolName, bool Allowed, string ReasonCode, IReadOnlyList<EvidenceRef> Evidence, bool Truncated);

/// <summary>Model-turn delegate: one bounded reasoning step returning either
/// tool requests or a final decision draft.</summary>
public sealed record EllaiModelTurn(
    IReadOnlyList<EllaiToolRequest> ToolRequests,
    EllaiDecision? FinalDecision,
    bool RequiresRepair = false,
    string? RejectionDiagnostic = null);

/// <summary>Per-iteration run gate. A refused gate stops the run with the
/// given safe blocked detail: "budget" for exhausted token/dollar budgets,
/// "denied" for revoked scope or consent.</summary>
public sealed record EllaiReauthorization(bool Permitted, string BlockedDetail);

/// <summary>Bounded multi-turn cycle runner. Each utterance is limited to
/// 12 model iterations, 4,000 model-visible tool calls, and 2 model-only schema
/// repairs so an unproductive provider loop cannot consume the session
/// budget. The $70/token budgets in <see cref="EllaiUsageLedger"/> remain
/// the hard spend governors.
/// Counters never reset on role change, provider fallback or rephrased
/// goals; one active request per investigation (concurrency safety, not
/// a quota).</summary>
public sealed class EllaiCycleRunner(EllaiToolBroker broker)
{
    public const int MaxIterations = 12;

    public const int MaxToolCalls = 4_000;

    public const int MaxRepairAttempts = 2;

    private int _activeRequests;

    public async Task<EllaiCycleOutcome> RunAsync(
        EllaiToolContext context,
        Func<int, Task<EllaiModelTurn>> modelTurn,
        Func<EllaiDecision, IReadOnlyList<string>> validate,
        CancellationToken ct,
        Func<int, CancellationToken, Task<EllaiReauthorization>>? reauthorizeAsync = null,
        EllaiActivityStatusCoordinator? activity = null,
        string cycleId = "cycle-1",
        long controllerEpoch = 1,
        EllaiSessionEventJournal? journal = null,
        Action<EllaiToolRequest, EllaiToolResult>? onToolResult = null,
        Func<CancellationToken, Task<bool>>? isExecutionCurrentAsync = null,
        Action<EllaiActivityEvent>? onActivity = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? invokeToolAsync = null)
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
            long sequence = 0;
            var traceGate = new object();
            var accepted = false;
            var lastTurnUnreviewed = false;

            void Trace(EllaiActivityKind kind, string detail = "", string? toolName = null)
            {
                lock (traceGate)
                {
                    var runtimeEvent = new EllaiActivityEvent(context.SessionId, controllerEpoch,
                        Interlocked.Increment(ref sequence), kind, cycleId, detail, DateTimeOffset.UtcNow, toolName);
                    onActivity?.Invoke(runtimeEvent);
                    if (activity is null)
                    {
                        return;
                    }

                    var status = activity.Publish(runtimeEvent);
                    if (status is not null)
                    {
                        journal?.Append(
                            context.SessionId, "activity", status.Phase, status.Text,
                            DateTimeOffset.UtcNow);
                    }
                }
            }

            async Task<EllaiToolResult> InvokeObservedAsync(EllaiToolRequest request, int remainingCalls)
            {
                Trace(EllaiActivityKind.ToolStarted, ActivityDetail(request.ToolName), request.ToolName);
                using var timing = EllaiPipelineMetrics.Measure("tool_execution");
                var toolContext = context with { RemainingCalls = remainingCalls };
                var invocation = invokeToolAsync is null ? broker.InvokeAsync(toolContext, request, ct)
                    : invokeToolAsync(toolContext, request, ct);
                using var progressDelay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    for (var update = 0; update < 2 && !invocation.IsCompleted; update++)
                    {
                        var delay = Task.Delay(TimeSpan.FromSeconds(update == 0 ? 8 : 20), progressDelay.Token);
                        if (await Task.WhenAny(invocation, delay).ConfigureAwait(false) == invocation
                            || invocation.IsCompleted || ct.IsCancellationRequested) break;
                        Trace(EllaiActivityKind.ToolPending, ActivityDetail(request.ToolName), request.ToolName);
                    }
                    return await invocation.ConfigureAwait(false);
                }
                finally { await progressDelay.CancelAsync().ConfigureAwait(false); }
            }

            EllaiCycleOutcome Stop(string reason, int iteration, string blockedDetail, string? failureCode = null)
            {
                Trace(EllaiActivityKind.CycleBlocked, blockedDetail);
                return new EllaiCycleOutcome(false, null, reason, iteration, toolCallsUsed, repairUsed,
                    failureCode ?? (string.IsNullOrEmpty(blockedDetail) ? "InvalidDecision" : blockedDetail));
            }

            for (var iteration = 1; iteration <= MaxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();
                if (reauthorizeAsync is not null)
                {
                    var gate = await reauthorizeAsync(iteration, ct);
                    if (gate is null || !gate.Permitted)
                    {
                        return Stop($"Run authorization failed at iteration {iteration}.", iteration,
                            gate?.BlockedDetail ?? "denied");
                    }
                }

                var turn = await modelTurn(iteration);
                if (turn.RequiresRepair)
                {
                    if (repairsAttempted >= MaxRepairAttempts)
                        return Stop(turn.RejectionDiagnostic ?? "The model did not produce a valid decision within the repair allowance.",
                            iteration, "invalid", "InvalidDecision");
                    repairsAttempted++;
                    repairUsed = true;
                    continue;
                }
                lastTurnUnreviewed = turn.ToolRequests.Count > 0 && turn.FinalDecision is null;

                // The accepted check-in fires on the first real tool request,
                // never for a pure small-talk turn: a greeting answers directly
                // with no "looking up" status. A later iteration revisits the
                // evidence only after observations exist.
                if (turn.ToolRequests.Count > 0)
                {
                    if (!accepted)
                    {
                        Trace(EllaiActivityKind.OperationAccepted);
                        accepted = true;
                    }

                    Trace(EllaiActivityKind.ReasoningTurnStarted);
                }
                else if (iteration > 1 && accepted)
                {
                    Trace(EllaiActivityKind.ReasoningTurnStarted);
                }

                var needsArgumentRepair = false;
                // Parallel reads require independent persistence scopes supplied by
                // the Gateway. The ordinary scoped broker remains serial.
                foreach (var batch in turn.ToolRequests.Chunk(invokeToolAsync is null ? 1 : 4))
                {
                    if (isExecutionCurrentAsync is not null
                        && !await isExecutionCurrentAsync(ct).ConfigureAwait(false))
                    {
                        return Stop("The session or its processing permission changed before the tool call.",
                            iteration, "denied");
                    }

                    if (toolCallsUsed + batch.Length > MaxToolCalls)
                    {
                        return Stop("Model-visible tool budget exhausted.", iteration, "budget");
                    }

                    var results = await Task.WhenAll(batch.Select((request, index) =>
                        InvokeObservedAsync(request, MaxToolCalls - toolCallsUsed - index))).ConfigureAwait(false);
                    toolCallsUsed += batch.Length;
                    for (var index = 0; index < batch.Length; index++)
                    {
                        var toolRequest = batch[index];
                        var result = results[index];
                        onToolResult?.Invoke(toolRequest, result);
                        if (!result.Allowed)
                        {
                            if (result.ReasonCode == EllaiToolReasonCodes.InvalidArguments)
                            {
                                // The rejected call did not execute. Its error observation is already
                                // in the model thread; allow correction within the existing call/turn limits.
                                // Never accept a decision packaged with the rejected call.
                                needsArgumentRepair = true;
                                continue;
                            }
                            return Stop($"Tool {toolRequest.ToolName} denied: {result.DenialReason}",
                                iteration, BlockedDetail(result.ReasonCode), result.ReasonCode);
                        }

                        Trace(EllaiActivityKind.ToolObservationAccepted);
                    }
                }

                if (needsArgumentRepair)
                {
                    continue;
                }

                if (turn.FinalDecision is not null)
                {
                    var errors = validate(turn.FinalDecision);
                    if (errors.Count == 0)
                    {
                        // A tool-less run (pure small talk) answers with no
                        // status at all; completion projects only after real
                        // tool work. Blocks always project (see Stop).
                        if (accepted)
                        {
                            Trace(EllaiActivityKind.CycleCompleted, "verified");
                        }

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

            // A final turn of reads with no decision and no room left is an
            // honest unreviewed stop (RT-55), not a budget exhaustion: the
            // observation was accepted and stored, but never reasoned on.
            return Stop("Reasoning iteration limit reached before a final decision.", MaxIterations,
                lastTurnUnreviewed ? "unreviewed" : "iterations");
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
