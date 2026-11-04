using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiConversationContextTests
{
    [Fact]
    public void UsesAllPriorCompletedPairsInOrder()
    {
        var turns = Enumerable.Range(1, 9).Select(index => new EllaiConversationTurn(
            "session", $"u-{index}", index, $"question {index}", $"answer {index}",
            index == 7 ? "TerminalFailure" : "Completed", 1, DateTimeOffset.UtcNow)).ToArray();
        var context = EllaiConversationContext.Build(turns, "u-9");
        Assert.Equal(14, context.Count);
        Assert.Equal("question 1", context[0].Content);
        Assert.Equal("answer 8", context[^1].Content);
        Assert.DoesNotContain(context, message => message.Content.Contains("7") || message.Content.Contains("9"));
        Assert.All(context.Where((_, index) => index % 2 == 0), message => Assert.Equal("user", message.Role));
        Assert.All(context.Where((_, index) => index % 2 == 1), message => Assert.Equal("assistant", message.Role));
        Assert.Empty(EllaiConversationContext.Build(turns, "unknown"));
    }

    [Fact]
    public void IncludesCompletePairBeyondFormerCharacterBound()
    {
        EllaiConversationTurn[] turns =
        [
            new("s", "u-1", 1, new string('a', 8000), new string('b', 8000), "Completed", 1, DateTimeOffset.UtcNow),
            new("s", "u-2", 2, "current", null, "Processing", 1, null),
        ];
        var context = EllaiConversationContext.Build(turns, "u-2");
        Assert.Equal(2, context.Count);
        Assert.Equal(turns[0].UserText, context[0].Content);
        Assert.Equal(turns[0].AssistantText, context[1].Content);
    }
}
