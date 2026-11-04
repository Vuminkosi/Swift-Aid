using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;
using System.Text.Json;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiFrameworkUsageTests
{
    [Fact]
    public async Task StructuredResponseSchemaCountsTowardsReservationAndMissingUsageEstimate()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"description\":\"" + new string('a', 600) + "\"}");
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var plain = new ChatOptions { MaxOutputTokens = 100 };
        var structured = new ChatOptions { MaxOutputTokens = 100,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement, "decision") };
        var store = new InMemoryEllaiUsageStore();
        var usage = new EllaiFrameworkUsage("session", new EllaiUsageLedger(sessionTokenBudget: 500), store);

        Assert.True(usage.CanDispatch(messages, plain));
        Assert.False(usage.CanDispatch(messages, structured));
        await usage.RecordAsync(new ChatResponse(), messages, structured, default);
        var totals = await store.TotalsAsync("session", default);
        Assert.Equal(0, totals.PromptTokens);
        Assert.True(totals.TotalTokens >= 700);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task NonPositiveProviderCountsAreEstimatedRatherThanTreatedAsFreeUsage(int reported)
    {
        var store = new InMemoryEllaiUsageStore();
        var ledger = new EllaiUsageLedger();
        await new EllaiFrameworkUsage("session", ledger, store).RecordAsync(
            new ChatResponse { Usage = new UsageDetails { InputTokenCount = reported, OutputTokenCount = reported } },
            [new ChatMessage(ChatRole.User, "hello")], new ChatOptions { MaxOutputTokens = 100 }, default);
        var totals = await store.TotalsAsync("session", default);
        Assert.Equal(0, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
        Assert.True(totals.TotalTokens > 100);
    }

    [Fact]
    public async Task InMemoryStoreRejectsNegativeEstimates()
    {
        var store = new InMemoryEllaiUsageStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RecordAsync(
            new StoredUsageSample("session", 0, 0, 0, DateTimeOffset.UtcNow) { EstimatedDollars = -1 }, default));
        Assert.Equal(0, (await store.TotalsAsync("session", default)).Calls);
    }

    [Fact]
    public async Task EstimatedCostLimitsBudgetWithoutBeingReportedAsActualSpend()
    {
        var prices = new EllaiPriceTable(1m, 1m, 0m);
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 10000, envelopeSpendCap: 1m, prices: prices);
        var store = new InMemoryEllaiUsageStore();
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        await new EllaiFrameworkUsage("session", ledger, store).RecordAsync(new ChatResponse(), messages,
            new ChatOptions { MaxOutputTokens = 500 }, default);
        Assert.Equal(0m, await store.SpendForAsync("session", default));
        Assert.Equal(0m, ledger.SpendFor("session"));
        Assert.True(await store.BudgetSpendForAsync("session", default) > 0.5m);
        var restored = new EllaiUsageLedger(sessionTokenBudget: 10000, envelopeSpendCap: 1m, prices: prices);
        await restored.HydrateAsync("session", store, default);
        Assert.Equal(0m, restored.SpendFor("session"));
        using var reservation = restored.TryBeginModelCall("session", 500);
        Assert.Null(reservation);
    }

    [Fact]
    public async Task MissingUsageReservesEstimatedBudgetAcrossHydrationWithoutInventingReportedTokens()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 1000);
        var store = new InMemoryEllaiUsageStore();
        var usage = new EllaiFrameworkUsage("session", ledger, store);
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var options = new ChatOptions { MaxOutputTokens = 100 };
        await usage.RecordAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")), messages, options, default);
        var stored = await store.TotalsAsync("session", default);
        Assert.Equal(0, stored.PromptTokens);
        Assert.Equal(0, stored.CompletionTokens);
        Assert.True(stored.TotalTokens > 100);
        var restored = new EllaiUsageLedger(sessionTokenBudget: 1000);
        await restored.HydrateAsync("session", store, default);
        Assert.Equal(stored.TotalTokens, restored.TotalsFor("session").TotalTokens);
        Assert.Equal(ledger.TotalsFor("session").TotalTokens, restored.TotalsFor("session").TotalTokens);
    }

    [Fact]
    public async Task PartialUsageEstimatesOnlyMissingTokenCount()
    {
        var ledger = new EllaiUsageLedger();
        var store = new InMemoryEllaiUsageStore();
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        await new EllaiFrameworkUsage("session", ledger, store).RecordAsync(
            new ChatResponse { Usage = new UsageDetails { InputTokenCount = 7 } }, messages,
            new ChatOptions { MaxOutputTokens = 100 }, default);
        var totals = await store.TotalsAsync("session", default);
        Assert.Equal(7, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
        Assert.Equal(107, totals.TotalTokens);
    }

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
        }, messages, options, CancellationToken.None);

        var totals = await store.TotalsAsync("session", CancellationToken.None);
        Assert.Equal(490, totals.PromptTokens);
        Assert.Equal(490, totals.CompletionTokens);
        Assert.False(usage.CanDispatch(messages, options));
    }
}
