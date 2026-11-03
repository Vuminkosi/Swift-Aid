namespace SwiftAid.Application.Ellai;

/// <summary>A terminal processing result, separate from queue delivery acknowledgement.</summary>
public sealed record EllaiUtteranceResolution(string Outcome, string? AssistantText, long? ExpectedInteractionEpoch)
{
    /// <summary>Bounded model proposals retained for later authorization; never executable receipts.</summary>
    public string? UiProposalsJson { get; init; }
    /// <summary>Server-observed tool references for this turn, not authorization to replay it.</summary>
    public string? ToolTraceJson { get; init; }
    public string? FrameworkSessionJson { get; init; }
    public string? ExpectedOwnerActorId { get; init; }
    public string? ExpectedScopeEpoch { get; init; }

    public static EllaiUtteranceResolution Completed(string text, long epoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new("Completed", text, epoch);
    }

    public static EllaiUtteranceResolution Failed { get; } = new("TerminalFailure", null, null);
    public static EllaiUtteranceResolution Cancelled { get; } = new("Cancelled", null, null);

    public void Validate()
    {
        if (FrameworkSessionJson is not null && (Outcome != "Completed" ||
            System.Text.Encoding.UTF8.GetByteCount(FrameworkSessionJson) > 1_048_576))
            throw new ArgumentException("Only completed decisions may retain a bounded framework session.");
        if (ToolTraceJson is not null)
        {
            if (Outcome != "Completed" || !EllaiToolTrace.IsValid(ToolTraceJson))
                throw new ArgumentException("Only completed decisions may retain a valid bounded tool trace.");
        }
        if (UiProposalsJson is not null)
        {
            if (Outcome != "Completed") throw new ArgumentException("Only completed decisions may retain UI proposals.");
            _ = EllaiUiProposalPayload.Deserialize(UiProposalsJson);
        }
        if (Outcome is not ("Completed" or "TerminalFailure" or "Cancelled")
            || (Outcome == "Completed" && (string.IsNullOrWhiteSpace(AssistantText)
                || ExpectedInteractionEpoch is null))
            || (Outcome != "Completed" && AssistantText is not null))
        {
            throw new ArgumentException("A terminal resolution must contain a valid outcome and matching response.");
        }
    }
}

public sealed record EllaiConversationTurn(
    string SessionId, string ClientUtteranceId, long Sequence, string? UserText,
    string? AssistantText, string Outcome, int AttemptCount, DateTimeOffset? CompletedAtUtc)
{
    public string? UiProposalsJson { get; init; }
    public string? ToolTraceJson { get; init; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; init; }
}
