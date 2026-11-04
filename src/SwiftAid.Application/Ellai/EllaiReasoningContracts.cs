using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>A provider message in an Ellai conversation.</summary>
public sealed record EllaiChatMessage(
    string Role,
    string Content,
    IReadOnlyList<EllaiToolRequest>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>A completion and its provider-reported token usage.</summary>
public sealed record EllaiTurnCompletion(string Text, int PromptTokens, int CompletionTokens)
{
    public string? FinishReason { get; init; }
}

/// <summary>Completion client used by existing non-agent guidance planning.</summary>
public interface IEllaiTurnCompletionClient
{
    Task<EllaiTurnCompletion> CompleteAsync(
        IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct);

    async Task<EllaiTurnCompletion> CompleteStreamingAsync(
        IReadOnlyList<EllaiChatMessage> messages, int maxTokens,
        Func<string, CancellationToken, ValueTask> onText, CancellationToken ct)
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
    string InteractionMode = "typed",
    string? GuidanceState = null)
{
    public string? FrameworkSessionJson { get; init; }
    public Func<string, CancellationToken, ValueTask>? OnVerifiedStreamSegment { get; init; }
    public Func<IReadOnlyList<EllaiObservedToolResult>, CancellationToken, Task<bool>>? RevalidateStreamObservations { get; init; }
}

/// <summary>A parsed decision or a bounded set of tool requests.</summary>
public sealed record EllaiModelTurn(
    IReadOnlyList<EllaiToolRequest> ToolRequests,
    EllaiDecision? FinalDecision,
    bool RequiresRepair = false,
    string? RejectionDiagnostic = null);

/// <summary>Execution boundary for a single authorized utterance.</summary>
public interface IEllaiReasoningExecutor
{
    Task<EllaiCycleOutcome> RunAsync(
        EllaiLoopRequest request, CancellationToken ct,
        Func<int, CancellationToken, Task<bool>>? reauthorizeAsync = null,
        EllaiActivityStatusCoordinator? activity = null,
        EllaiSessionEventJournal? journal = null,
        Func<CancellationToken, Task<bool>>? isExecutionCurrentAsync = null,
        Func<string, CancellationToken, ValueTask>? onTextDelta = null,
        Action<EllaiActivityEvent>? onActivity = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? invokeToolAsync = null);
}
