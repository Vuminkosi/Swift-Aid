namespace SwiftAid.Application.Ellai;

/// <summary>Completed exchanges selected from the durable execution history.</summary>
public static class EllaiConversationContext
{
    /// <summary>Historical value retained for source compatibility; it is not a selection limit.</summary>
    public const int MaximumTurns = 6;
    /// <summary>Historical value retained for source compatibility; it is not a selection limit.</summary>
    public const int MaximumCharacters = 12_000;

    public static IReadOnlyList<EllaiChatMessage> Build(
        IReadOnlyList<EllaiConversationTurn> conversation, string currentUtteranceId)
    {
        return SelectTurns(conversation, currentUtteranceId).SelectMany(turn => new[]
        {
            new EllaiChatMessage("user", turn.UserText!),
            new EllaiChatMessage("assistant", turn.AssistantText!),
        }).ToArray();
    }

    public static IReadOnlyList<EllaiConversationTurn> SelectTurns(
        IReadOnlyList<EllaiConversationTurn> conversation, string currentUtteranceId)
    {
        var current = conversation.FirstOrDefault(turn => turn.ClientUtteranceId == currentUtteranceId);
        if (current is null) return [];
        var pairs = new List<EllaiConversationTurn>();
        foreach (var turn in conversation.Where(turn => turn.SessionId == current.SessionId && turn.Sequence < current.Sequence
                     && turn.Outcome == "Completed" && !string.IsNullOrWhiteSpace(turn.UserText)
                     && !string.IsNullOrWhiteSpace(turn.AssistantText)).OrderByDescending(turn => turn.Sequence))
        {
            pairs.Add(turn);
        }
        pairs.Reverse();
        return pairs;
    }
}
