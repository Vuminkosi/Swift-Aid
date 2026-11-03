using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using Microsoft.AspNetCore.Http;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiUtteranceWorkerOutcomeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ToolWorkStartsBeforeSpeechAndPublishesProgressThenDurableOutcome(bool fail, bool superseded)
    {
        var store = new InMemoryEllaiSessionStore();
        var speech = new NarrationSpeech();
        using var prepared = new EllaiPreparedResponseAudio(speech, NullLogger<EllaiPreparedResponseAudio>.Instance);
        await prepared.StartAsync(CancellationToken.None);
        var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "tool-test"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("tool-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, CancellationToken.None);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Show my assigned cases", "cases", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            new HashSet<string> { EllaiToolNames.State }, EllaiConsentPurposes.PublicProductHelp, "tool-u");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, "tool-u",
            CancellationToken.None, EllaiUtteranceWorkEnvelope.Serialize(work, "tool-u"));
        var consent = new EllaiConsentService();
        var queue = new HeldQueue(fail);
        var completion = new ToolCompletion();
        var loop = new EllaiReasoningLoop(new EllaiCycleRunner(new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue)),
            new EllaiPromptBuilder("Synthetic test policy", "v-test"), completion);
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<IEllaiReasoningExecutor>(loop)
            .AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent, queue: queue)).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using (var parallelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                while (queue.Calls < 2) await Task.Delay(10, parallelTimeout.Token);
            Assert.False(queue.Release.Task.IsCompleted);
            var ack = Assert.Single(responses.ReadSince(session.SessionId, 0).Responses,
                row => row.Progress?.State == "Acknowledgement");
            Assert.Equal("Processing", Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, CancellationToken.None)).Outcome);
            // The real Gateway PCM endpoint consumes the same transient narration while the tool is held open.
            var http = new DefaultHttpContext();
            http.Response.Body = new MemoryStream();
            var audio = await EllaiGuestEndpoints.StreamResponseAudioAsync(session.SessionId, ack.Sequence,
                http, responses, prepared, new(), new(), Guid.NewGuid());
            await audio.ExecuteAsync(http);
            Assert.Equal("Let me check the records.", speech.Text);
            Assert.False(queue.Release.Task.IsCompleted);
            if (superseded)
                await store.TryUpdateAsync(session with { SessionVersion = 3, InteractionEpoch = 2 }, 2, CancellationToken.None);
            if (!fail && !superseded)
            {
                using var pendingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!responses.ReadSince(session.SessionId, 0).Responses.Any(row => row.Progress is { State: "Progress", Speak: true }))
                    await Task.Delay(25, pendingTimeout.Token);
            }
            queue.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal(superseded ? "Cancelled" : fail ? "TerminalFailure" : "Completed", turn.Outcome);
            Assert.DoesNotContain("Let me", turn.AssistantText ?? "", StringComparison.Ordinal);
            var events = responses.ReadSince(session.SessionId, 0, 64).Responses;
            Assert.Single(events, row => row.Progress?.State == "Acknowledgement");
            Assert.Equal(superseded ? "Cancelled" : fail ? "Failed" : "Completed", events[^1].Progress?.State);
            Assert.False(responses.CanSpeakProgress(ack));
            Assert.Equal(fail || superseded ? 2 : 3, queue.Calls);
        }
        finally
        {
            queue.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            await prepared.StopAsync(CancellationToken.None);
        }
    }

    private sealed class HeldQueue(bool fail) : ICaseQueueQuery
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public async Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new(fail ? CaseQueueAccess.Unavailable : CaseQueueAccess.Allowed, []);
        }
    }

    private sealed class ToolCompletion : IEllaiTurnCompletionClient
    {
        private int _calls;
        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
            Task.FromResult(new EllaiTurnCompletion(++_calls switch
            {
                1 => """{"tool_calls":[{"name":"ellai_state","args":{"projection":"queue"},"id":"queue-1"},{"name":"ellai_state","args":{"projection":"queue"},"id":"queue-2"}]}""",
                2 => """{"tool_calls":[{"name":"ellai_state","args":{"projection":"queue"},"id":"queue-3"}]}""",
                _ => Answer,
            }, 10, 10));
    }

    private sealed class NarrationSpeech : IEllaiSpeechSynthesisClient
    {
        public bool IsConfigured => true;
        public string? Text { get; private set; }
        public Task<HttpResponseMessage> SynthesizeAsync(string requestId, string text, CancellationToken cancellationToken)
        {
            Text = text;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0, 0, 0]) };
            response.Content.Headers.ContentType = new("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Voice-Id", "swiftaid-mms-english-v1");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            return Task.FromResult(response);
        }
    }

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
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<IEllaiReasoningExecutor>(loop).BuildServiceProvider();
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
