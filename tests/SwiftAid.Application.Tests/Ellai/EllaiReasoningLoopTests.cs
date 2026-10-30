using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiReasoningLoopTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true);

    private sealed class ScriptedCompletions : IEllaiTurnCompletionClient
    {
        private readonly Queue<string> _replies;

        public ScriptedCompletions(params string[] replies) => _replies = new Queue<string>(replies);

        public List<IReadOnlyList<EllaiChatMessage>> Threads { get; } = new();

        public int Calls { get; private set; }

        public Task<EllaiTurnCompletion> CompleteAsync(
            IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            Calls++;
            Threads.Add(messages.ToList());
            return Task.FromResult(new EllaiTurnCompletion(_replies.Dequeue(), 10, 5));
        }
    }

    private static EllaiToolBroker GrantedBroker()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent);
    }

    private static EllaiReasoningLoop Loop(
        ScriptedCompletions completions, EllaiUsageLedger? ledger = null) =>
        new(new EllaiCycleRunner(GrantedBroker()),
            new EllaiPromptBuilder("Policy text.", "v-test"), completions, ledger);

    private static EllaiLoopRequest Request(EllaiToolContext? context = null) => new(
        context ?? Context, "What is the current time?", new EllaiRoleOverlay("ask", "Be brief."),
        "clock", 256, "cycle-1", 1);

    private const string DecisionTurn = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"It is now.",
         "spokenText":"Now.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
         "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

    [Fact]
    public async Task ToolResultEntersLaterTurnWithCallId()
    {
        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"c1","name":"ellai_now","args":{}}]}""",
            DecisionTurn);
        var loop = Loop(completions);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(1, outcome.ToolCallsUsed);
        Assert.Equal(2, completions.Calls);
        var second = completions.Threads[1];
        var toolMessage = Assert.Single(second, m => m.Role == "tool");
        Assert.Contains("c1", toolMessage.Content, StringComparison.Ordinal);
        Assert.Contains("ellai_now", toolMessage.Content, StringComparison.Ordinal);
        Assert.Contains("Current server time", toolMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseFailureRepairsInsideLivingThread()
    {
        var completions = new ScriptedCompletions("not json at all", DecisionTurn);
        var loop = Loop(completions);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(2, completions.Calls);
        var second = completions.Threads[1];
        Assert.Contains(second, m => m.Role == "user" && m.Content.Contains("Turn rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BudgetExhaustionStopsWithBudgetDetail()
    {
        var ledger = new EllaiUsageLedger(sessionTokenBudget: 5);
        var completions = new ScriptedCompletions(DecisionTurn);
        var loop = Loop(completions, ledger);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(0, completions.Calls);
        Assert.Contains("authorization", outcome.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsageAccruesToLedgerPerTurn()
    {
        var ledger = new EllaiUsageLedger();
        var completions = new ScriptedCompletions(DecisionTurn);
        var loop = Loop(completions, ledger);

        var outcome = await loop.RunAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Completed);
        var totals = ledger.TotalsFor("session-1");
        Assert.Equal(1, totals.Calls);
        Assert.Equal(15, totals.TotalTokens);
    }

    [Fact]
    public async Task SmallTalkTurnEmitsNoActivity()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var loop = new EllaiReasoningLoop(
            new EllaiCycleRunner(GrantedBroker()),
            new EllaiPromptBuilder("Policy text.", "v-test"), completions);
        var activity = new EllaiActivityStatusCoordinator();
        var journal = new EllaiSessionEventJournal();

        var outcome = await loop.RunAsync(Request(), CancellationToken.None, activity: activity, journal: journal);

        Assert.True(outcome.Completed);
        Assert.Null(activity.CurrentStatus("session-1", 1));
        Assert.Empty(journal.ReadSince("session-1", 0).Events);
    }

    [Fact]
    public async Task InvalidRequestsFailFast()
    {
        var loop = Loop(new ScriptedCompletions());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            loop.RunAsync(Request() with { Utterance = "  " }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            loop.RunAsync(Request() with { MaxTokensPerTurn = 0 }, CancellationToken.None));
    }
}
