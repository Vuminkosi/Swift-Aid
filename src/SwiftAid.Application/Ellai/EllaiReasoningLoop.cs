using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>One chat message in a reasoning thread. Roles are provider
/// primitives (system/user/assistant/tool); content is prompt text, never
/// credentials or record payloads beyond the tool-result summaries.</summary>
public sealed record EllaiChatMessage(string Role, string Content);

/// <summary>Text completion for one reasoning turn with provider-reported
/// token usage for the ledger. The loop parses <see cref="Text"/> through
/// <see cref="EllaiModelTurnParser"/>; free prose is never a tool call.</summary>
public sealed record EllaiTurnCompletion(string Text, int PromptTokens, int CompletionTokens);

/// <summary>Text-completion lane for one reasoning turn. Implemented in
/// Intelligence over the routed provider (Meta/Ollama/serverless); the loop
/// never sees credentials or transport. Throws on transport failure so the
/// run stops instead of reasoning on invented turns.</summary>
public interface IEllaiTurnCompletionClient
{
    Task<EllaiTurnCompletion> CompleteAsync(
        IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct);
}

/// <summary>One utterance bound for multi-turn reasoning.</summary>
public sealed record EllaiLoopRequest(
    EllaiToolContext Context,
    string Utterance,
    EllaiRoleOverlay Role,
    string CapabilitySummary,
    int MaxTokensPerTurn,
    string CycleId,
    long ControllerEpoch);

/// <summary>Multi-turn reasoning loop (Task 6): drives the bounded cycle
/// runner with a routed text provider. Each model text turn parses to typed
/// tool calls or a validated final decision; accepted tool results re-enter
/// the next turn under their call ids, so the model reasons on real
/// observations in call-ID order. Provider usage accrues to the ledger and
/// the per-iteration gate refuses new calls once budgets or scope lapse.
/// Parse failures feed back as a repair hint inside the living thread and
/// consume ordinary iterations — never silent drops, never an extra
/// unmetered lane. Thread-carried result summaries are truncated past
/// <see cref="ThreadObservationChars"/> (marked); the authoritative bounded
/// result stays re-readable by re-invoking the read within budget.</summary>
public sealed class EllaiReasoningLoop(
    EllaiCycleRunner runner,
    EllaiPromptBuilder prompts,
    IEllaiTurnCompletionClient completions,
    EllaiUsageLedger? ledger = null,
    IEllaiUsageStore? usageStore = null)
{
    public const string TurnProtocol =
        "Reply with exactly one JSON object and no other text. " +
        "To read: {\"tool_calls\":[{\"id\":\"c1\",\"name\":\"<tool>\",\"args\":{\"<arg>\":\"<value>\"}}]} " +
        "with string args only. " +
        "To finish: {\"final_decision\":{<EllaiDecision schema v2>}}. " +
        "Pure small talk answers directly through final_decision with no tool calls.";

    /// <summary>Thread-carried tool-result summary cap. Longer summaries are
    /// cut and marked; re-invoke the read for the full bounded result.</summary>
    public const int ThreadObservationChars = 4000;

    private readonly EllaiCycleRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly EllaiPromptBuilder _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    private readonly IEllaiTurnCompletionClient _completions =
        completions ?? throw new ArgumentNullException(nameof(completions));
    private readonly EllaiUsageLedger? _ledger = ledger;
    private readonly IEllaiUsageStore? _usageStore = usageStore;

    public async Task<EllaiCycleOutcome> RunAsync(
        EllaiLoopRequest request,
        CancellationToken ct,
        Func<int, CancellationToken, Task<bool>>? reauthorizeAsync = null,
        EllaiActivityStatusCoordinator? activity = null,
        EllaiSessionEventJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.Role);
        if (string.IsNullOrWhiteSpace(request.Utterance) || request.Utterance.Length > 8000)
        {
            throw new ArgumentException("Utterance must be 1..8000 characters.", nameof(request));
        }

        if (request.MaxTokensPerTurn is < 1 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var limits = $"cycle {request.CycleId}: {EllaiCycleRunner.MaxIterations} iterations, " +
            $"{EllaiCycleRunner.MaxToolCalls} model-visible tool calls, " +
            $"{EllaiCycleRunner.MaxRepairAttempts} model-only repairs" +
            (_ledger is null
                ? string.Empty
                : $", {Math.Max(0, _ledger.RemainingFor(request.Context.SessionId))} session tokens left");
        var system = _prompts.Build(request.Role, request.CapabilitySummary, limits) +
            "\n## Turn protocol\n" + TurnProtocol +
            "\n## Tool catalogue\n" + EllaiToolBroker.DescribeCatalogue();
        var thread = new List<EllaiChatMessage> { new("user", request.Utterance) };

        async Task<EllaiModelTurn> TurnAsync(int iteration)
        {
            var messages = new List<EllaiChatMessage> { new("system", system) };
            messages.AddRange(thread);
            var completion = await _completions.CompleteAsync(
                messages, request.MaxTokensPerTurn, ct).ConfigureAwait(false);
            var sample = new EllaiUsageSample(
                request.Context.SessionId,
                Math.Max(0, completion.PromptTokens), Math.Max(0, completion.CompletionTokens));
            _ledger?.Record(sample);
            if (_usageStore is not null)
            {
                await _usageStore.RecordAsync(new StoredUsageSample(
                    sample.SessionId, sample.PromptTokens, sample.CompletionTokens,
                    _ledger?.DollarsFor(sample) ?? 0m, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            }
            thread.Add(new EllaiChatMessage("assistant", completion.Text));
            var parsed = EllaiModelTurnParser.Parse(completion.Text);
            if (parsed.Turn is not null)
            {
                return parsed.Turn;
            }

            thread.Add(new EllaiChatMessage("user",
                $"Turn rejected ({parsed.Error}) Reply with exactly one valid JSON turn."));
            return new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), null);
        }

        Task<EllaiReauthorization> GateAsync(int iteration, CancellationToken gateCt)
        {
            return ReauthorizeAsync(request, reauthorizeAsync, iteration, gateCt);
        }

        void ObserveTool(EllaiToolRequest toolRequest, EllaiToolResult result)
        {
            var summary = result.Allowed
                ? result.ResultSummary
                : $"denied ({result.ReasonCode}): {result.DenialReason}";
            if (summary.Length > ThreadObservationChars)
            {
                summary = summary[..ThreadObservationChars] + "… [truncated for context]";
            }

            thread.Add(new EllaiChatMessage("tool",
                $"call {toolRequest.CallId ?? toolRequest.ToolName}: " +
                $"{toolRequest.ToolName} [{result.ReasonCode}] {summary}"));
        }

        return await _runner.RunAsync(
            request.Context, TurnAsync, EllaiDecisionValidator.Validate, ct, GateAsync,
            activity, request.CycleId, request.ControllerEpoch, journal, ObserveTool).ConfigureAwait(false);
    }

    private async Task<EllaiReauthorization> ReauthorizeAsync(
        EllaiLoopRequest request,
        Func<int, CancellationToken, Task<bool>>? reauthorizeAsync,
        int iteration, CancellationToken ct)
    {
        if (reauthorizeAsync is not null && !await reauthorizeAsync(iteration, ct).ConfigureAwait(false))
        {
            return new EllaiReauthorization(false, "denied");
        }

        if (_ledger is not null && !_ledger.TryReserve(request.Context.SessionId, request.MaxTokensPerTurn))
        {
            return new EllaiReauthorization(false, "budget");
        }

        return new EllaiReauthorization(true, string.Empty);
    }
}
