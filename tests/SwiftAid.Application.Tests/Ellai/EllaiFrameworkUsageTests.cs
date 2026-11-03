using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiFrameworkUsageTests
{
    [Fact]
    public async Task RecordsProviderUsageAndDeniesLaterCallWhenBudgetIsExhausted()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 1000);
        var store = new InMemoryEllaiUsageStore();
        var usage = new EllaiFrameworkUsage("session", ledger, store);
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var options = new ChatOptions { MaxOutputTokens = 10 };
        Assert.True(usage.CanDispatch(messages, options));

        await usage.RecordAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 490, OutputTokenCount = 490 }
        }, CancellationToken.None);

        var totals = await store.TotalsAsync("session", CancellationToken.None);
        Assert.Equal(490, totals.PromptTokens);
        Assert.Equal(490, totals.CompletionTokens);
        Assert.False(usage.CanDispatch(messages, options));
    }
}
