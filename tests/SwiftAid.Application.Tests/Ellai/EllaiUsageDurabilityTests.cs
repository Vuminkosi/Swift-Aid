using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiUsageDurabilityTests
{
    private sealed class ScriptedCompletions : IEllaiTurnCompletionClient
    {
        private readonly Queue<string> _replies;

        public ScriptedCompletions(params string[] replies) => _replies = new Queue<string>(replies);

        public Task<EllaiTurnCompletion> CompleteAsync(
            IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
            Task.FromResult(new EllaiTurnCompletion(_replies.Dequeue(), 100, 50));
    }

    private const string DecisionTurn = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.",
         "spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
         "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

    private static EllaiReasoningLoop Loop(
        ScriptedCompletions completions, EllaiUsageLedger ledger, IEllaiUsageStore store) =>
        new(new EllaiCycleRunner(new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService())),
            new EllaiPromptBuilder("Policy text.", "v-test"), completions, ledger, store);

    private static EllaiLoopRequest Request(string session = "session-1") => new(
        new EllaiToolContext(session, "epoch-1", "cases", "case-1", 4, true,
            "consent/1", EllaiProcessingClassification.SyntheticDemo),
        "Hello.", new EllaiRoleOverlay("ask", "Be brief."),
        "clock", 256, "cycle-1", 1);

    [Fact]
    public async Task LoopPersistsPricedSamplesPerTurn()
    {
        var prices = new EllaiPriceTable(0.01m, 0.02m, 0m);
        var ledger = new EllaiUsageLedger(prices: prices);
        var store = new InMemoryEllaiUsageStore();
        var loop = Loop(new ScriptedCompletions(DecisionTurn), ledger, store);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Completed);
        var totals = await store.TotalsAsync("session-1", CancellationToken.None);
        Assert.Equal(100, totals.PromptTokens);
        Assert.Equal(50, totals.CompletionTokens);
        Assert.Equal(1, totals.Calls);
        // 100/1000 * 0.01 + 50/1000 * 0.02 = 0.002
        Assert.Equal(0.002m, await store.SpendForAsync("session-1", CancellationToken.None));
        Assert.Equal(0.002m, await store.TotalSpendAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HydrateRestoresCountersAfterRestart()
    {
        var prices = new EllaiPriceTable(0.01m, 0.02m, 0m);
        var store = new InMemoryEllaiUsageStore();
        await store.RecordAsync(
            new StoredUsageSample("session-1", 1000, 500, 0.02m, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var ledger = new EllaiUsageLedger(sessionTokenBudget: 2_000, prices: prices);
        await ledger.HydrateAsync("session-1", store, CancellationToken.None);

        Assert.Equal(1500, ledger.TotalsFor("session-1").TotalTokens);
        Assert.Equal(0.02m, ledger.SpendFor("session-1"));
        Assert.Equal(0.02m, ledger.TotalSpend);
        Assert.False(ledger.TryReserve("session-1", 501));
    }

    [Fact]
    public async Task HydrateConvergesByMaximum()
    {
        var store = new InMemoryEllaiUsageStore();
        await store.RecordAsync(
            new StoredUsageSample("session-1", 100, 50, 0m, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var ledger = new EllaiUsageLedger();
        ledger.Record(new EllaiUsageSample("session-1", 400, 200));
        await ledger.HydrateAsync("session-1", store, CancellationToken.None);

        // Memory already ahead: no double-count.
        var totals = ledger.TotalsFor("session-1");
        Assert.Equal(400, totals.PromptTokens);
        Assert.Equal(1, totals.Calls);
    }
}
