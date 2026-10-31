using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiUtteranceWorkerOutcomeTests
{
    private const string Answer = """
        {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic answer.",
        "spokenText":"Synthetic answer.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,
        "topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
        """;

    [Theory]
    [InlineData(true, false, "Completed")]
    [InlineData(false, false, "TerminalFailure")]
    [InlineData(false, true, "Cancelled")]
    public async Task WorkerAcknowledgesOnlyWithDurableExplicitOutcome(bool valid, bool superseded, string expectedOutcome)
    {
        var store = new InMemoryEllaiSessionStore();
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "worker-test"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("worker-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, CancellationToken.None);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "u-1");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, "u-1",
            CancellationToken.None, EllaiUtteranceWorkEnvelope.Serialize(work, "u-1"));
        var consent = new EllaiConsentService();
        var loop = new EllaiReasoningLoop(new EllaiCycleRunner(new EllaiToolBroker(new EllaiPolicyService(), consent)),
            new EllaiPromptBuilder("Synthetic test policy", "v-test"), new Completion(valid ? Answer : "{}",
                superseded ? async () =>
                {
                    await store.TryUpdateAsync(session with { SessionVersion = 3, InteractionEpoch = 2 },
                        2, CancellationToken.None);
                } : null));
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton(loop).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal(expectedOutcome, turn.Outcome);
            Assert.Equal("Synthetic question", turn.UserText);
            Assert.Equal(valid ? "Synthetic answer." : null, turn.AssistantText);
            var final = responses.ReadSince(session.SessionId, 0).Responses.Last();
            Assert.True(final.IsReconciliation);
            Assert.Equal(expectedOutcome, final.Outcome);
            Assert.Equal(turn.AssistantText ?? "", final.Text);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class Completion(string response, Func<Task>? beforeResponse = null) : IEllaiTurnCompletionClient
    {
        public async Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            if (beforeResponse is not null) await beforeResponse();
            return new EllaiTurnCompletion(response, 10, 10);
        }
    }
}
