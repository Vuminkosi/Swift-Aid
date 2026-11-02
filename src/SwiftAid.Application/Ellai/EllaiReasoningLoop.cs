using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>One chat message in a reasoning thread. Roles are provider
/// primitives (system/user/assistant/tool); content is prompt text, never
/// credentials or record payloads beyond the tool-result summaries.</summary>
public sealed record EllaiChatMessage(
    string Role,
    string Content,
    IReadOnlyList<EllaiToolRequest>? ToolCalls = null,
    string? ToolCallId = null);

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

    async Task<EllaiTurnCompletion> CompleteStreamingAsync(
        IReadOnlyList<EllaiChatMessage> messages,
        int maxTokens,
        Func<string, CancellationToken, ValueTask> onText,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onText);
        var completion = await CompleteAsync(messages, maxTokens, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(completion.Text))
        {
            await onText(completion.Text, ct).ConfigureAwait(false);
        }
        return completion;
    }
}

/// <summary>One utterance bound for multi-turn reasoning.</summary>
public sealed record EllaiLoopRequest(
    EllaiToolContext Context,
    string Utterance,
    EllaiRoleOverlay Role,
    string CapabilitySummary,
    int MaxTokensPerTurn,
    string CycleId,
    long ControllerEpoch,
    IReadOnlyList<EllaiChatMessage>? Conversation = null,
    string? ActivePage = null,
    string InteractionMode = "typed");

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

    private static readonly JsonSerializerOptions ContractJson = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private static readonly string DecisionSchema = JsonSchemaExporter
        .GetJsonSchemaAsNode(ContractJson, typeof(EllaiDecision)).ToJsonString();

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
        EllaiSessionEventJournal? journal = null,
        Func<CancellationToken, Task<bool>>? isExecutionCurrentAsync = null,
        Func<string, CancellationToken, ValueTask>? onTextDelta = null,
        Action<EllaiActivityEvent>? onActivity = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? invokeToolAsync = null)
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
            "\n## Current context\nServer time (UTC): " + DateTimeOffset.UtcNow.ToString("O") +
            ". Client-reported page: " + (EllaiPageContext.DisplayName(request.ActivePage) ?? "Unknown") +
            ". Page context is advisory; it grants no record access or tool authority. Do not infer a local time zone." +
            "\n" + EllaiInteractionContext.Describe(request.InteractionMode) +
            "\n## Turn protocol\n" + TurnProtocol +
            "\n## Final decision JSON schema\n" + DecisionSchema +
            "\nThe schema describes data shape, not permission. Claims must cite exact evidence objects returned by permitted tools. " +
            "Do not invent evidence references, UI bindings or action authority. Use empty arrays/null for nonapplicable fields." +
            "\n## Tool catalogue\n" + EllaiToolBroker.DescribeCatalogue(request.Context.AllowedTools);
        if (request.Context.AllowedTools is null || request.Context.AllowedTools.Contains(EllaiToolNames.UiCatalogue))
        {
            system += "\n## Interactive guidance\nFor requests to show, explain, or guide through application controls, " +
                "discover the relevant feature using ellai_ui_catalogue before proposing guidance. " +
                "Use ellai_ui_observe with view=current to inspect the latest client-reported state; unavailable controls are not proof of unavailable routes. " +
                "For a discovered supported feature, propose a StartGuidance UI intent with featureId=ellai.guidance, " +
                "resourceId=null, controlId=null and arguments containing only feature (the discovered feature ID) and objective (the user's goal). " +
                "The application validates and prepares the plan. Do not invent routes, anchors, completed actions or say the tour has started before application confirmation. " +
                "If discovery returns no eligible feature, explain the limitation without pretending to navigate.";
        }
        var thread = new List<EllaiChatMessage>(request.Conversation ?? []);
        thread.Add(new("user", request.Utterance));
        var provisionalAnswerRejected = false;
        var observedEvidence = new HashSet<EvidenceRef>();

        async Task<EllaiModelTurn> TurnAsync(int iteration)
        {
            var messages = new List<EllaiChatMessage> { new("system", system) };
            messages.AddRange(thread);
            var streamCurrentAnswer = onTextDelta is not null && !provisionalAnswerRejected;
            var emittedText = false;
            var textParser = streamCurrentAnswer ? new EllaiDecisionTextStreamParser() : null;
            var completion = !streamCurrentAnswer
                ? await _completions.CompleteAsync(messages, request.MaxTokensPerTurn, ct).ConfigureAwait(false)
                : await _completions.CompleteStreamingAsync(
                    messages,
                    request.MaxTokensPerTurn,
                    async (delta, callbackCt) =>
                    {
                        foreach (var sentence in textParser!.Append(delta))
                        {
                            emittedText = true;
                            await onTextDelta!(sentence, callbackCt).ConfigureAwait(false);
                        }
                    },
                    ct).ConfigureAwait(false);
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
            var validationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var parsed = EllaiModelTurnParser.Parse(completion.Text);
            var decisionErrors = parsed.Turn?.FinalDecision is { } decision
                ? EllaiDecisionValidator.Validate(decision)
                : Array.Empty<string>();
            if (parsed.Turn?.FinalDecision?.Claims is { } claims && claims.Any(claim =>
                    claim?.Evidence is { } evidence && evidence.Any(reference => reference is null || !observedEvidence.Contains(reference))))
                decisionErrors = decisionErrors.Append("Claim evidence must exactly match a reference returned by a permitted tool in this turn.").ToArray();
            EllaiPipelineMetrics.Record("decision_validation", System.Diagnostics.Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
            if (parsed.Turn is not null && decisionErrors.Count == 0)
            {
                if (textParser is not null && parsed.Turn.FinalDecision is { } finalDecision)
                {
                    var remainder = textParser.FlushVerified(finalDecision.DisplayText);
                    if (remainder.Length > 0)
                    {
                        await onTextDelta!(remainder, ct).ConfigureAwait(false);
                    }
                }

                if (parsed.Turn.ToolRequests.Count > 0)
                {
                    thread[^1] = thread[^1] with { ToolCalls = parsed.Turn.ToolRequests };
                }

                return parsed.Turn;
            }

            thread.Add(new EllaiChatMessage("user",
                $"Turn rejected ({parsed.Error ?? string.Join("; ", decisionErrors)}). Reply with exactly one valid JSON turn matching the active schema."));
            // The durable reconciliation replaces the rejected draft. Appending a repaired
            // draft here would duplicate or contradict text/audio already presented for this turn.
            provisionalAnswerRejected |= emittedText;
            var rejected = parsed.Turn?.FinalDecision;
            // Shape/count diagnostics only: never log provider prose, claim IDs or evidence payloads.
            var diagnostic = rejected is null ? "InvalidModelTurn" :
                $"InvalidDecision: schema={rejected.SchemaVersion}; displayLength={rejected.DisplayText?.Length ?? 0}; " +
                $"claimsNull={rejected.Claims is null}; nullClaims={rejected.Claims?.Count(c => c is null) ?? 0}; " +
                $"ungroundedClaims={rejected.Claims?.Count(c => c is not null && c.Kind is not (ClaimKind.Unknown or ClaimKind.Proposed) && (c.Evidence is null || c.Evidence.Count == 0)) ?? 0}; " +
                $"questionsNull={rejected.Questions is null}; intentsNull={rejected.UiIntents is null}; topicsNull={rejected.TopicRefs is null}; errors={decisionErrors.Count}";
            return new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), null, RequiresRepair: true, RejectionDiagnostic: diagnostic);
        }

        Task<EllaiReauthorization> GateAsync(int iteration, CancellationToken gateCt)
        {
            return ReauthorizeAsync(request, reauthorizeAsync, iteration,
                EstimateReservation(system, thread, request.MaxTokensPerTurn), gateCt);
        }

        void ObserveTool(EllaiToolRequest toolRequest, EllaiToolResult result)
        {
            if (result.Allowed)
                foreach (var reference in result.Evidence.Take(20)) observedEvidence.Add(reference);
            var summary = result.Allowed
                ? result.ResultSummary
                : $"denied ({result.ReasonCode}): {result.DenialReason}";
            if (summary.Length > ThreadObservationChars)
            {
                summary = summary[..ThreadObservationChars] + "… [truncated for context]";
            }

            thread.Add(new EllaiChatMessage("tool",
                $"call {toolRequest.CallId ?? toolRequest.ToolName}: " +
                $"{toolRequest.ToolName} [{result.ReasonCode}] {summary}" +
                "\nEvidence references: " + SerializeEvidence(result),
                ToolCallId: toolRequest.CallId ?? toolRequest.ToolName));
        }

        return await _runner.RunAsync(
            request.Context, TurnAsync, EllaiDecisionValidator.Validate, ct, GateAsync,
            activity, request.CycleId, request.ControllerEpoch, journal, ObserveTool,
            isExecutionCurrentAsync, onActivity, invokeToolAsync).ConfigureAwait(false);
    }

    private static string SerializeEvidence(EllaiToolResult result)
    {
        if (!result.Allowed) return "[]";
        var references = new List<string>();
        var remaining = 8000;
        foreach (var evidence in result.Evidence.Take(20))
        {
            var serialized = JsonSerializer.Serialize(evidence, ContractJson);
            if (serialized.Length > remaining) break;
            references.Add(serialized);
            remaining -= serialized.Length + 1;
        }
        return "[" + string.Join(",", references) + "]";
    }

    private async Task<EllaiReauthorization> ReauthorizeAsync(
        EllaiLoopRequest request,
        Func<int, CancellationToken, Task<bool>>? reauthorizeAsync,
        int iteration, int estimatedTokens, CancellationToken ct)
    {
        if (reauthorizeAsync is not null && !await reauthorizeAsync(iteration, ct).ConfigureAwait(false))
        {
            return new EllaiReauthorization(false, "denied");
        }

        if (_ledger is not null && !_ledger.TryReserve(request.Context.SessionId, estimatedTokens))
        {
            return new EllaiReauthorization(false, "budget");
        }

        return new EllaiReauthorization(true, string.Empty);
    }

    private static int EstimateReservation(
        string systemPrompt,
        IReadOnlyList<EllaiChatMessage> thread,
        int maxCompletionTokens)
    {
        // Provider usage showed prompts near 9k tokens while this lane only
        // reserved the 1k completion allowance. Reserve a conservative
        // character-to-token estimate for the full current thread before
        // every dispatch, including tool-call metadata and the next output.
        long utf8Bytes = Encoding.UTF8.GetByteCount(systemPrompt) + 32;
        foreach (var message in thread)
        {
            utf8Bytes += Encoding.UTF8.GetByteCount(message.Role) +
                Encoding.UTF8.GetByteCount(message.Content) + 16;
            if (message.ToolCallId is not null)
            {
                utf8Bytes += Encoding.UTF8.GetByteCount(message.ToolCallId);
            }

            if (message.ToolCalls is not null)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    utf8Bytes += Encoding.UTF8.GetByteCount(toolCall.ToolName) +
                        Encoding.UTF8.GetByteCount(toolCall.CallId ?? string.Empty) +
                        toolCall.Arguments.Sum(argument =>
                            Encoding.UTF8.GetByteCount(argument.Key) +
                            Encoding.UTF8.GetByteCount(argument.Value));
                }
            }
        }

        var promptEstimate = (utf8Bytes + 1) / 2;
        return (int)Math.Min(int.MaxValue, promptEstimate + maxCompletionTokens);
    }
}
