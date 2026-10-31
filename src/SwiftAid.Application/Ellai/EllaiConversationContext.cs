namespace SwiftAid.Application.Ellai;

/// <summary>Completed exchanges only, bounded independently of the durable history.</summary>
public static class EllaiConversationContext
{
    public const int MaximumTurns = 6;
    public const int MaximumCharacters = 12_000;

    public static IReadOnlyList<EllaiChatMessage> Build(
        IReadOnlyList<EllaiConversationTurn> conversation, string currentUtteranceId)
    {
        var current = conversation.FirstOrDefault(turn => turn.ClientUtteranceId == currentUtteranceId);
        if (current is null) return [];
        var pairs = new List<EllaiConversationTurn>();
        var characters = 0;
        foreach (var turn in conversation.Where(turn => turn.Sequence < current.Sequence
                     && turn.Outcome == "Completed" && !string.IsNullOrWhiteSpace(turn.UserText)
                     && !string.IsNullOrWhiteSpace(turn.AssistantText)).OrderByDescending(turn => turn.Sequence))
        {
            var size = turn.UserText!.Length + turn.AssistantText!.Length;
            if (pairs.Count == MaximumTurns || characters + size > MaximumCharacters) break;
            pairs.Add(turn);
            characters += size;
        }
        pairs.Reverse();
        return pairs.SelectMany(turn => new[]
        {
            new EllaiChatMessage("user", turn.UserText!),
            new EllaiChatMessage("assistant", turn.AssistantText!),
        }).ToArray();
    }
}
