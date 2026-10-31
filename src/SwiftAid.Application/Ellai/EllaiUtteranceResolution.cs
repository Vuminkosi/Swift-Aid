namespace SwiftAid.Application.Ellai;

/// <summary>A terminal processing result, separate from queue delivery acknowledgement.</summary>
public sealed record EllaiUtteranceResolution(string Outcome, string? AssistantText, long? ExpectedInteractionEpoch)
{
    public static EllaiUtteranceResolution Completed(string text, long epoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new("Completed", text, epoch);
    }

    public static EllaiUtteranceResolution Failed { get; } = new("TerminalFailure", null, null);
    public static EllaiUtteranceResolution Cancelled { get; } = new("Cancelled", null, null);

    public void Validate()
    {
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
    string? AssistantText, string Outcome, int AttemptCount, DateTimeOffset? CompletedAtUtc);
