using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiUtteranceRunnerTests
{
    private sealed class ScriptedCompletions : IEllaiTurnCompletionClient
    {
        private readonly Queue<string> _replies;

        public ScriptedCompletions(params string[] replies) => _replies = new Queue<string>(replies);

        public List<IReadOnlyList<EllaiChatMessage>> Threads { get; } = new();

        public Task<EllaiTurnCompletion> CompleteAsync(
            IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            Threads.Add(messages.ToList());
            return Task.FromResult(new EllaiTurnCompletion(_replies.Dequeue(), 10, 5));
        }
    }

    private const string DecisionTurn = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Hi there.",
         "spokenText":"Hi.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
         "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

    private static ActorContext TestActor()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[]
                {
                    new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.NameIdentifier, "officer-1"),
                    new System.Security.Claims.Claim(
                        ActorContext.AccessEpochClaimType, "7"),
                },
                "test"));
        return ActorContext.FromValidatedPrincipal(principal);
    }

    private static EllaiUtteranceWork Work() => new(
        "session-1", "epoch-1", "Hello.", "cases", "case-1", EllaiMode.Ask, 1, "cycle-1",
        TestActor(), true, "consent/1");

    private static (EllaiConsentService Consent, EllaiReasoningLoop Loop) Services(
        ScriptedCompletions completions, bool grant, EllaiUsageLedger? ledger = null)
    {
        var consent = new EllaiConsentService();
        if (grant)
        {
            consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
                EllaiToolBroker.ToolReadPurpose, EllaiUtteranceRunner.ConsentedLabel,
                TimeSpan.FromHours(1));
        }

        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var loop = new EllaiReasoningLoop(
            new EllaiCycleRunner(broker),
            new EllaiPromptBuilder("Policy text.", "v-test"), completions, ledger);
        return (consent, loop);
    }

    [Fact]
    public async Task MissingGrantRefusesBeforeAnyModelCall()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var (consent, loop) = Services(completions, grant: false);
        var journal = new EllaiSessionEventJournal();
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), journal, consent);

        var outcome = await EllaiUtteranceRunner.RunOneAsync(Work(), services, 256, CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.False(outcome.Completed);
        Assert.Empty(completions.Threads);
        var events = journal.ReadSince("session-1", 0).Events;
        var blocked = Assert.Single(events);
        Assert.Equal("activity", blocked.Kind);
        Assert.Equal("blocked", blocked.Ref);
    }

    [Fact]
    public async Task GrantedSmallTalkCompletesWithSystemPrompt()
    {
        var completions = new ScriptedCompletions(DecisionTurn);
        var ledger = new EllaiUsageLedger();
        var (consent, loop) = Services(completions, grant: true, ledger);
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), new EllaiSessionEventJournal(), consent);

        var outcome = await EllaiUtteranceRunner.RunOneAsync(Work(), services, 256, CancellationToken.None);

        Assert.True(outcome.Started);
        Assert.True(outcome.Completed);
        var system = Assert.Single(completions.Threads)[0];
        Assert.Equal("system", system.Role);
        Assert.Contains("Turn protocol", system.Content, StringComparison.Ordinal);
        Assert.Contains("ellai_now", system.Content, StringComparison.Ordinal);
        Assert.Equal(1, ledger.TotalsFor("session-1").Calls);
    }

    [Fact]
    public async Task GrantedToolTurnReadsLiveClock()
    {
        var completions = new ScriptedCompletions(
            """{"tool_calls":[{"id":"c1","name":"ellai_now","args":{}}]}""",
            DecisionTurn.Replace("Hi there.", "It is now."));
        var (consent, loop) = Services(completions, grant: true);
        var services = new EllaiUtteranceServices(
            loop, new EllaiActivityStatusCoordinator(), new EllaiSessionEventJournal(), consent);

        var outcome = await EllaiUtteranceRunner.RunOneAsync(
            Work() with { Utterance = "What time is it?" }, services, 256, CancellationToken.None);

        Assert.True(outcome.Started);
        Assert.True(outcome.Completed);
        var second = completions.Threads[1];
        var toolMessage = Assert.Single(second, m => m.Role == "tool");
        Assert.Contains("call c1", toolMessage.Content, StringComparison.Ordinal);
    }
}
