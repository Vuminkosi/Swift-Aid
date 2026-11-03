using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Cases;
using Microsoft.AspNetCore.Http;
using SwiftAid.Gateway.Endpoints;
using Microsoft.Extensions.AI;
using SwiftAid.Intelligence.Meta;
using System.Runtime.CompilerServices;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiUtteranceWorkerOutcomeTests
{
    [Fact]
    public async Task FeedHidesProvisionalChunkAfterAnotherAttemptReclaimsTurn()
    {
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "feed-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("feed-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(session, default);
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2 }, 1, "turn", default, "{}");
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(await store.ClaimNextAsync(now, now.AddMinutes(1), default));
        var responses = new EllaiSessionResponseStore();
        var generation = responses.CaptureGeneration(session.SessionId);
        Assert.True(responses.PublishProvisional(session.SessionId, generation, "turn", 1, "scope", 1, 1, "private segment"));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "feed-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")) };
        var first = (EllaiSessionResponsePageDto)((IValueHttpResult)await EllaiConversationFeed.ReadAsync(
            http, session.SessionId, 0, null, store, responses, store, default)).Value!;
        Assert.Contains(first.Responses, item => item.Text == "private segment");
        Assert.NotNull(await store.ClaimNextAsync(now.AddMinutes(2), now.AddMinutes(3), default));
        var after = (EllaiSessionResponsePageDto)((IValueHttpResult)await EllaiConversationFeed.ReadAsync(
            http, session.SessionId, 0, null, store, responses, store, default)).Value!;
        Assert.DoesNotContain(after.Responses, item => item.Text == "private segment");
        await using var services = new ServiceCollection().AddSingleton<IEllaiSessionStore>(store)
            .AddSingleton<IEllaiUtteranceOutboxStore>(store).BuildServiceProvider();
        http.RequestServices = services;
        using var prepared = new EllaiPreparedResponseAudio(new NarrationSpeech(), NullLogger<EllaiPreparedResponseAudio>.Instance);
        var audio = await EllaiGuestEndpoints.StreamResponseAudioAsync(session.SessionId, 1, http,
            responses, prepared, new(), new());
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)audio).StatusCode);
    }

    [Fact]
    public async Task FeedHidesProvisionalChunkWhenAttemptIsReclaimedDuringRead()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "feed-race-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
        var actor = ActorContext.FromValidatedPrincipal(principal);
        var now = DateTimeOffset.UtcNow;
        var session = new EllaiSessionRecord("feed-race-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, now);
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(session, default);
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2 }, 1, "turn", default, "{}");
        Assert.NotNull(await store.ClaimNextAsync(now, now.AddMinutes(1), default));
        var responses = new EllaiSessionResponseStore();
        var generation = responses.CaptureGeneration(session.SessionId);
        Assert.True(responses.PublishProvisional(session.SessionId, generation, "turn", 1, "scope", 1, 1, "private segment"));
        var outbox = new CompletionObservedOutbox(store)
        {
            AfterRead = async () => Assert.NotNull(await store.ClaimNextAsync(now.AddMinutes(2), now.AddMinutes(3), default))
        };
        var http = new DefaultHttpContext { User = principal };

        var page = (EllaiSessionResponsePageDto)((IValueHttpResult)await EllaiConversationFeed.ReadAsync(
            http, session.SessionId, 0, null, outbox, responses, store, default)).Value!;
        Assert.DoesNotContain(page.Responses, item => item.Text == "private segment");
    }

    [Fact]
    public async Task FeedOmitsConversationWhenOwnerChangesDuringRead()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "old-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
        var actor = ActorContext.FromValidatedPrincipal(principal);
        var session = new EllaiSessionRecord("feed-owner-race", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(session, default);
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2 }, 1, "turn", default, "{}");
        var outbox = new CompletionObservedOutbox(store)
        {
            AfterRead = async () => Assert.NotNull(await store.TryUpdateAsync(
                session with { SessionVersion = 3, OwnerActorId = "new-owner" }, 2, default))
        };
        var page = (EllaiSessionResponsePageDto)((IValueHttpResult)await EllaiConversationFeed.ReadAsync(
            new DefaultHttpContext { User = principal }, session.SessionId, 0, null,
            outbox, new EllaiSessionResponseStore(), store, default)).Value!;
        Assert.Empty(page.Conversation!);
    }

    [Fact]
    public async Task AudioHidesProvisionalChunkWhenAttemptIsReclaimedDuringRead()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "audio-race-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
        var actor = ActorContext.FromValidatedPrincipal(principal);
        var now = DateTimeOffset.UtcNow;
        var session = new EllaiSessionRecord("audio-race-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, now);
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(session, default);
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2 }, 1, "turn", default, "{}");
        Assert.NotNull(await store.ClaimNextAsync(now, now.AddMinutes(1), default));
        var responses = new EllaiSessionResponseStore();
        var generation = responses.CaptureGeneration(session.SessionId);
        Assert.True(responses.PublishProvisional(session.SessionId, generation, "turn", 1, "scope", 1, 1, "private segment"));
        var outbox = new CompletionObservedOutbox(store)
        {
            AfterRead = async () => Assert.NotNull(await store.ClaimNextAsync(now.AddMinutes(2), now.AddMinutes(3), default))
        };
        await using var services = new ServiceCollection().AddSingleton<IEllaiSessionStore>(store)
            .AddSingleton<IEllaiUtteranceOutboxStore>(outbox).BuildServiceProvider();
        var http = new DefaultHttpContext { User = principal, RequestServices = services };
        using var prepared = new EllaiPreparedResponseAudio(new NarrationSpeech(), NullLogger<EllaiPreparedResponseAudio>.Instance);

        var audio = await EllaiGuestEndpoints.StreamResponseAudioAsync(session.SessionId, 1, http,
            responses, prepared, new(), new());
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)audio).StatusCode);
    }

    [Fact]
    public async Task ProvisionalDeliveryFailsClosedWhenDurableCheckIsUnavailable()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "outage-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
        var actor = ActorContext.FromValidatedPrincipal(principal);
        var now = DateTimeOffset.UtcNow;
        var session = new EllaiSessionRecord("outage-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, now);
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(session, default);
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2 }, 1, "turn", default, "{}");
        Assert.NotNull(await store.ClaimNextAsync(now, now.AddMinutes(1), default));
        var responses = new EllaiSessionResponseStore();
        var generation = responses.CaptureGeneration(session.SessionId);
        Assert.True(responses.PublishProvisional(session.SessionId, generation, "turn", 1, "scope", 1, 1, "private segment"));
        var outbox = new CompletionObservedOutbox(store) { FailDelivery = true };
        await using var services = new ServiceCollection().AddSingleton<IEllaiSessionStore>(store)
            .AddSingleton<IEllaiUtteranceOutboxStore>(outbox).BuildServiceProvider();
        var http = new DefaultHttpContext { User = principal, RequestServices = services };
        using var prepared = new EllaiPreparedResponseAudio(new NarrationSpeech(), NullLogger<EllaiPreparedResponseAudio>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EllaiConversationFeed.ReadAsync(http, session.SessionId, 0, null, outbox, responses, store, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EllaiGuestEndpoints.StreamResponseAudioAsync(session.SessionId, 1, http, responses, prepared, new(), new()));
    }

    [Fact]
    public async Task FrameworkGuidanceProposalSurvivesWorkerCommitAndOwnerProjection()
    {
        var store = new InMemoryEllaiSessionStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "guidance-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("guidance-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Show me the report form", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "guidance-turn");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        const string response = """
            {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Let me show you the report form.",
             "spokenText":"Let me show you the report form.","claims":[],"questions":[],
             "uiIntents":[{"kind":"StartGuidance","featureId":"ellai.guidance","resourceId":null,"controlId":null,
               "arguments":{"feature":"report","objective":"Open the report form"}}],
             "planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
            """;
        var consent = new EllaiConsentService();
        using var client = new OutcomeChatClient(response);
        var executor = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton(new EllaiSessionResponseStore()).AddSingleton(consent)
            .AddSingleton<IEllaiReasoningExecutor>(executor).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("Completed", turn.Outcome);
            var proposal = Assert.Single(EllaiConversationProjection.ToClient(turn).UiProposals);
            Assert.Equal(UiIntentKind.StartGuidance, proposal.Proposal.Kind);
            Assert.Equal("report", proposal.Proposal.Arguments["feature"]);
            Assert.Empty(await store.ReadConversationAsync(session.SessionId, "another-owner", timeout.Token));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PublicationFailureAfterCommitDoesNotRetryCompletedFrameworkTurn()
    {
        var store = new InMemoryEllaiSessionStore();
        var outbox = new CompletionObservedOutbox(store);
        var audio = new FailingAudioPreparation();
        var responses = new EllaiSessionResponseStore(audioPreparation: audio);
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "publication-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("publication-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "turn");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        using var model = new HeldFrameworkStream("Synthetic answer.");
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(outbox)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<IEllaiReasoningExecutor>(executor).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await model.FirstPart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.Release.TrySetResult();
            await audio.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await outbox.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, default));
            Assert.Equal("Completed", turn.Outcome);
            Assert.Equal("Synthetic answer.", turn.AssistantText);
            Assert.Equal(0, outbox.FailCount);
        }
        finally
        {
            model.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FailingAudioPreparation : IEllaiResponseAudioPreparation
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Published(EllaiSessionResponseDto response)
        {
            Called.TrySetResult();
            throw new InvalidOperationException("synthetic audio failure");
        }
        public void Clear(string sessionId) { }
    }

    [Fact]
    public async Task RenewedInMemoryClaimCannotBeReclaimedUntilNewExpiry()
    {
        var store = new InMemoryEllaiSessionStore();
        var now = DateTimeOffset.UtcNow;
        var session = new EllaiSessionRecord("renew-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, "owner", "scope", null, null, now);
        await store.CreateAsync(session, default);
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, "turn", default, "{}");
        Assert.NotNull(await store.ClaimNextAsync(now, now.AddSeconds(10), default));
        Assert.True(await store.RenewLeaseAsync(session.SessionId, "turn", 1, now.AddSeconds(5), now.AddSeconds(20), default));
        Assert.Null(await store.ClaimNextAsync(now.AddSeconds(11), now.AddSeconds(30), default));
        var reclaimed = await store.ClaimNextAsync(now.AddSeconds(21), now.AddSeconds(31), default);
        Assert.Equal(2, reclaimed!.AttemptCount);
        Assert.False(await store.RenewLeaseAsync(session.SessionId, "turn", 1, now.AddSeconds(22), now.AddSeconds(40), default));
        Assert.True(await store.RenewLeaseAsync(session.SessionId, "turn", 2, now.AddSeconds(22), now.AddSeconds(40), default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FrameworkWorkerRenewsLeaseOrCancelsIfRenewalFails(bool rejectRenewal, bool noncooperative)
    {
        var store = new InMemoryEllaiSessionStore();
        var outbox = new CompletionObservedOutbox(store) { RejectRenewal = rejectRenewal };
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "lease-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("lease-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "lease-u");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        using var model = new HeldFrameworkStream("Synthetic answer.");
        var framework = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        var uncooperative = new CancellationIgnoringExecutor();
        IEllaiReasoningExecutor executor = noncooperative ? uncooperative : framework;
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(outbox)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<IEllaiReasoningExecutor>(executor).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance, TimeSpan.FromMilliseconds(100));
        await worker.StartAsync(default);
        try
        {
            await (noncooperative ? uncooperative.Started.Task : model.FirstPart.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await outbox.RenewalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!rejectRenewal)
            {
                model.Release.TrySetResult();
                var completion = await outbox.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("Completed", completion!.Outcome);
                return;
            }
            if (noncooperative)
            {
                await uncooperative.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                uncooperative.Release.TrySetResult();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.Outcome == "Processing");
            Assert.Equal("RetryableFailure", turn.Outcome);
            Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                item => item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
        }
        finally
        {
            model.Release.TrySetResult();
            uncooperative.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CancellationIgnoringExecutor : IEllaiReasoningExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EllaiCycleOutcome> RunAsync(EllaiLoopRequest request, CancellationToken ct,
            Func<int, CancellationToken, Task<bool>>? reauthorizeAsync = null, EllaiActivityStatusCoordinator? activity = null,
            EllaiSessionEventJournal? journal = null, Func<CancellationToken, Task<bool>>? isExecutionCurrentAsync = null,
            Func<string, CancellationToken, ValueTask>? onTextDelta = null, Action<SwiftAid.Application.Ellai.EllaiActivityEvent>? onActivity = null,
            Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? invokeToolAsync = null)
        {
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            await Release.Task;
            var decision = EllaiModelTurnParser.Parse(Answer).Turn!.FinalDecision!;
            return new EllaiCycleOutcome(true, decision, null, 1, 0, false);
        }
    }

    [Theory]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(true, false, false, false, true, false)]
    [InlineData(true, false, false, false, false, true)]
    public async Task FrameworkWorkerPublishesOnlyValidatedStreamAndPreparesGatewayAudio(bool multipleChunks, bool reclaimed, bool cancelBeforeCommit, bool failRenewalDuringCommit, bool clearAfterCommit, bool remoteCancellationAfterCommit)
    {
        var store = new InMemoryEllaiSessionStore();
        var speech = new NarrationSpeech();
        using var prepared = new EllaiPreparedResponseAudio(speech, NullLogger<EllaiPreparedResponseAudio>.Instance);
        await prepared.StartAsync(CancellationToken.None);
        var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "stream-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("stream-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "stream-u");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        var outbox = new CompletionObservedOutbox(store)
        {
            HoldCompletion = cancelBeforeCommit || failRenewalDuringCommit,
            AfterCompletion = clearAfterCommit ? id => { responses.Clear(id); return Task.CompletedTask; }
                : remoteCancellationAfterCommit ? async id =>
                {
                    var current = (await store.GetAsync(id, default))!;
                    Assert.NotNull(await store.TryUpdateAsync(current with
                        { SessionVersion = current.SessionVersion + 1, InteractionEpoch = current.InteractionEpoch + 1 }, current.SessionVersion, default));
                } : null
        };
        var answer = multipleChunks ? new string('a', 80) + ". Second sentence." : "Synthetic answer.";
        using var model = new HeldFrameworkStream(answer);
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(outbox)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<IEllaiReasoningExecutor>(executor).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance,
            cancelBeforeCommit || failRenewalDuringCommit ? TimeSpan.FromMilliseconds(25) : null);
        await worker.StartAsync(default);
        try
        {
            await model.FirstPart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                item => item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
            if (reclaimed)
            {
                var now = DateTimeOffset.UtcNow.AddMinutes(11);
                var nextClaim = await store.ClaimNextAsync(now, now.AddMinutes(10), default);
                Assert.NotNull(nextClaim);
                Assert.Equal(2, nextClaim.AttemptCount);
            }
            model.Release.TrySetResult();
            if (failRenewalDuringCommit)
            {
                await outbox.CompletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                outbox.RejectRenewal = true;
                using var failureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                EllaiConversationTurn failed;
                do
                {
                    await Task.Delay(10, failureTimeout.Token);
                    failed = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, failureTimeout.Token));
                } while (failed.Outcome == "Processing");
                Assert.Equal("RetryableFailure", failed.Outcome);
                Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                    item => item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
                return;
            }
            if (clearAfterCommit)
            {
                Assert.Equal("Completed", (await outbox.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)))!.Outcome);
                await outbox.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(responses.ReadSince(session.SessionId, 0).Responses);
                Assert.Empty(speech.Requests);
                return;
            }
            if (remoteCancellationAfterCommit)
            {
                Assert.Equal("Completed", (await outbox.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)))!.Outcome);
                await outbox.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                    item => item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
                Assert.Empty(speech.Requests);
                return;
            }
            if (cancelBeforeCommit)
            {
                await outbox.CompletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var renewalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (outbox.RenewalCount < 2) await Task.Delay(10, renewalTimeout.Token);
                Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                    item => item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
                var current = await store.GetAsync(session.SessionId, default);
                Assert.NotNull(current);
                Assert.NotNull(await store.TryUpdateAsync(current with
                    { SessionVersion = current.SessionVersion + 1, InteractionEpoch = current.InteractionEpoch + 1 }, current.SessionVersion, default));
                outbox.ReleaseCompletion.TrySetResult();
                var cancelled = await outbox.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("Cancelled", cancelled!.Outcome);
                Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                    item => item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
                return;
            }
            if (reclaimed)
            {
                Assert.Null(await outbox.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                    item => item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("Completed", turn.Outcome);
            var chunks = responses.ReadSince(session.SessionId, 0).Responses
                .Where(item => !item.IsFinal && item.Progress is null && !string.IsNullOrWhiteSpace(item.Text)).ToArray();
            Assert.Equal(multipleChunks ? 2 : 1, chunks.Length);
            Assert.Equal(answer, string.Join(" ", chunks.Select(item => item.Text)));
            Assert.All(chunks, chunk => Assert.Equal(work.ClientUtteranceId, chunk.ClientUtteranceId));
            foreach (var chunk in chunks)
            {
                var http = new DefaultHttpContext();
                using var body = new MemoryStream();
                http.Response.Body = body;
                var result = await EllaiGuestEndpoints.StreamResponseAudioAsync(session.SessionId, chunk.Sequence,
                    http, responses, prepared, new(), new(), Guid.NewGuid());
                await result.ExecuteAsync(http);
                Assert.Equal(new byte[] { 0, 0, 0, 0 }, body.ToArray());
            }
            Assert.All(chunks, chunk => Assert.Contains(chunk.Text, speech.Requests));
        }
        finally
        {
            model.Release.TrySetResult();
            outbox.ReleaseCompletion.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            await prepared.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CompletionObservedOutbox(IEllaiUtteranceOutboxStore inner) : IEllaiUtteranceOutboxStore, IEllaiUtteranceLeaseRenewal, IEllaiProvisionalDeliveryFence
    {
        public bool HoldCompletion { get; init; }
        public Func<string, Task>? AfterCompletion { get; init; }
        public Func<Task>? AfterRead { get; init; }
        public bool FailDelivery { get; init; }
        private volatile bool _rejectRenewal;
        public bool RejectRenewal { get => _rejectRenewal; set => _rejectRenewal = value; }
        private int _renewalCount;
        private int _claimCount;
        private int _failCount;
        public int FailCount => Volatile.Read(ref _failCount);
        public TaskCompletionSource SecondClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RenewalCount => Volatile.Read(ref _renewalCount);
        public TaskCompletionSource RenewalObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompletionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EllaiUtteranceResolution?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EllaiConversationTurn?> ReadLatestGuidanceTurnAsync(string sessionId, string ownerActorId, CancellationToken ct) =>
            inner.ReadLatestGuidanceTurnAsync(sessionId, ownerActorId, ct);
        public async Task<IReadOnlyList<EllaiConversationTurn>> ReadConversationAsync(string sessionId, string ownerActorId, CancellationToken ct)
        {
            var result = await inner.ReadConversationAsync(sessionId, ownerActorId, ct);
            if (AfterRead is not null) await AfterRead();
            return result;
        }
        public Task<bool> CanDeliverProvisionalAsync(string sessionId, string utteranceId, int attempt, string actorId,
            string scopeEpoch, long interactionEpoch, CancellationToken ct) =>
            FailDelivery ? Task.FromException<bool>(new InvalidOperationException("Synthetic durable read failure")) :
            inner is IEllaiProvisionalDeliveryFence fence
                ? fence.CanDeliverProvisionalAsync(sessionId, utteranceId, attempt, actorId, scopeEpoch, interactionEpoch, ct)
                : Task.FromResult(false);
        public Task<IReadOnlyList<EllaiConversationTurn>> ReadConversationForExecutionAsync(string sessionId, string ownerActorId,
            string clientUtteranceId, CancellationToken ct) =>
            inner.ReadConversationForExecutionAsync(sessionId, ownerActorId, clientUtteranceId, ct);
        public async Task<EllaiUtteranceOutboxItem?> ClaimNextAsync(DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct)
        {
            var result = await inner.ClaimNextAsync(nowUtc, leaseExpiresAtUtc, ct);
            if (Interlocked.Increment(ref _claimCount) >= 2) SecondClaim.TrySetResult();
            return result;
        }
        public async Task<EllaiUtteranceResolution?> CompleteAsync(string sessionId, string clientUtteranceId, int attemptCount,
            DateTimeOffset completedAtUtc, CancellationToken ct, EllaiUtteranceResolution? resolution = null)
        {
            CompletionStarted.TrySetResult();
            if (HoldCompletion) await ReleaseCompletion.Task.WaitAsync(ct);
            var result = await inner.CompleteAsync(sessionId, clientUtteranceId, attemptCount, completedAtUtc, ct, resolution);
            if (result is not null && AfterCompletion is not null) await AfterCompletion(sessionId);
            Completion.TrySetResult(result);
            return result;
        }
        public Task FailAsync(string sessionId, string clientUtteranceId, int attemptCount, DateTimeOffset retryAtUtc, bool terminal, CancellationToken ct)
        {
            Interlocked.Increment(ref _failCount);
            return inner.FailAsync(sessionId, clientUtteranceId, attemptCount, retryAtUtc, terminal, ct);
        }
        public async Task<bool> RenewLeaseAsync(string sessionId, string clientUtteranceId, int attemptCount,
            DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct)
        {
            var result = !RejectRenewal && await ((IEllaiUtteranceLeaseRenewal)inner).RenewLeaseAsync(
                sessionId, clientUtteranceId, attemptCount, nowUtc, leaseExpiresAtUtc, ct);
            Interlocked.Increment(ref _renewalCount);
            RenewalObserved.TrySetResult();
            return result;
        }
    }

    [Fact]
    public async Task FrameworkWorkerBuffersEvidenceFreeSegmentUntilModelFinishes()
    {
        var store = new InMemoryEllaiSessionStore();
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "segment-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("segment-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "segment-u");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        using var model = new HeldFrameworkStream("Synthetic answer.", segmented: true);
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        await using var services = new ServiceCollection().AddSingleton<IEllaiSessionStore>(store)
            .AddSingleton<IEllaiUtteranceOutboxStore>(store).AddSingleton(responses).AddSingleton(consent)
            .AddSingleton<IEllaiReasoningExecutor>(executor).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await model.FirstPart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                item => item.Text == "Synthetic answer.");
            Assert.Equal("Processing", Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token)).Outcome);
            model.Release.TrySetResult();
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("Completed", turn.Outcome);
            Assert.Equal("Synthetic answer.", turn.AssistantText);
        }
        finally { model.Release.TrySetResult(); await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class HeldFrameworkStream(string answer, bool segmented = false) : IChatClient
    {
        public TaskCompletionSource FirstPart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = Answer.Replace("Synthetic answer.", answer, StringComparison.Ordinal);
            if (segmented)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "{\"stream_segments\":[{\"text\":\"Synthetic answer.\",\"claims\":[]}]");
                FirstPart.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "," + response.TrimStart()[1..]);
                yield break;
            }
            var split = response.IndexOf(answer, StringComparison.Ordinal) + answer.Length;
            yield return new ChatResponseUpdate(ChatRole.Assistant, response[..split]);
            FirstPart.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response[split..]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task FrameworkWorkerRetriesProviderOutageAndCompletesTheSameTurn()
    {
        var store = new InMemoryEllaiSessionStore();
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "framework-retry"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("retry-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "retry-u");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        using var model = new TransientFailureChatClient();
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<IEllaiReasoningExecutor>(executor)
            .BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(25, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);

            Assert.Equal("Completed", turn.Outcome);
            Assert.Equal("Synthetic answer.", turn.AssistantText);
            Assert.Equal(2, turn.AttemptCount);
            Assert.Equal(2, model.Calls);
            Assert.Single(responses.ReadSince(session.SessionId, 0).Responses,
                item => item.IsReconciliation && item.Text == "Synthetic answer.");
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class TransientFailureChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (++Calls == 1) throw new HttpRequestException("synthetic provider outage");
            yield return new ChatResponseUpdate(ChatRole.Assistant, Answer);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task WorkerExecutesSplitProviderToolNameThroughFramework()
    {
        var store = new InMemoryEllaiSessionStore();
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "split-worker"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("split-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "What time is it?", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.Now },
            EllaiConsentPurposes.PublicProductHelp, "turn");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        using var handler = new SplitToolHttpHandler();
        using var http = new HttpClient(handler);
        using var model = EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key", ReasoningModel = "fixture-model"
        }, http);
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        await using var services = new ServiceCollection().AddSingleton<IEllaiSessionStore>(store)
            .AddSingleton<IEllaiUtteranceOutboxStore>(store).AddSingleton(responses).AddSingleton(consent)
            .AddSingleton<IEllaiReasoningExecutor>(executor).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("Completed", turn.Outcome);
            Assert.Equal("Synthetic answer.", turn.AssistantText);
            Assert.Equal(2, handler.Calls);
            Assert.True(EllaiToolTrace.TryRead(turn.ToolTraceJson!, out var trace));
            Assert.Contains(trace!.Observations, item => item.ToolName == EllaiToolNames.Now &&
                item.ClockVersion == EllaiObservedToolResult.CurrentClockVersion);
        }
        finally { await worker.StopAsync(default); }
    }

    private sealed class SplitToolHttpHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = ++Calls == 1 ? """
                data: {"id":"fixture","object":"chat.completion.chunk","created":1,"model":"fixture-model","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"ellai_","arguments":"{"}}]},"finish_reason":null}]}

                data: {"id":"fixture","object":"chat.completion.chunk","created":1,"model":"fixture-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"now","arguments":"}"}}]},"finish_reason":null}]}

                data: {"id":"fixture","object":"chat.completion.chunk","created":1,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

                data: [DONE]

                """ : "data: " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = "fixture", @object = "chat.completion.chunk", created = 1, model = "fixture-model",
                    choices = new[] { new { index = 0, delta = new { role = "assistant", content = Answer }, finish_reason = "stop" } }
                }) + "\n\ndata: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/event-stream")
            });
        }
    }

    [Fact]
    public async Task WorkerRejectsMalformedToolArgumentsWithoutPublishingAnAnswer()
    {
        var store = new InMemoryEllaiSessionStore();
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "malformed-worker"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("malformed-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "What time is it?", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.Now },
            EllaiConsentPurposes.PublicProductHelp, "turn");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        using var model = new MalformedWorkerClient();
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        await using var services = new ServiceCollection().AddSingleton<IEllaiSessionStore>(store)
            .AddSingleton<IEllaiUtteranceOutboxStore>(store).AddSingleton(responses).AddSingleton(consent)
            .AddSingleton<IEllaiReasoningExecutor>(executor).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(10, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("TerminalFailure", turn.Outcome);
            Assert.Null(turn.AssistantText);
            Assert.Null(turn.ToolTraceJson);
            Assert.Equal(1, model.Calls);
            Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                item => item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
        }
        finally { await worker.StopAsync(default); }
    }

    private sealed class MalformedWorkerClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Calls++;
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("malformed", EllaiToolNames.Now) { Exception = new FormatException("synthetic parse failure") }]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(null, false, false, false)]
    [InlineData(EllaiToolNames.Now, false, false, false)]
    [InlineData(EllaiToolNames.Capabilities, false, false, false)]
    [InlineData(EllaiToolNames.Receipts, false, false, false)]
    [InlineData(EllaiToolNames.UiCatalogue, false, false, false)]
    [InlineData(EllaiToolNames.UiCatalogue, true, false, false)]
    [InlineData(EllaiToolNames.UiCatalogue, false, true, false)]
    [InlineData(EllaiToolNames.UiObserve, false, false, false)]
    [InlineData(EllaiToolNames.UiObserve, false, false, true)]
    public async Task WorkerRunsFrameworkExecutorAndPersistsAndStreamsAnswer(string? toolName, bool changeCatalogue, bool filterGoal,
        bool changeObservationBeforeAnswer)
    {
        var store = new InMemoryEllaiSessionStore();
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "framework-worker"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("framework-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, CancellationToken.None);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Synthetic question", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0",
            EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
            toolName is null ? new HashSet<string>() : new HashSet<string> { toolName }, EllaiConsentPurposes.PublicProductHelp, "framework-u");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, "framework-u",
            CancellationToken.None, EllaiUtteranceWorkEnvelope.Serialize(work, "framework-u"));
        var consent = new EllaiConsentService();
        var usage = new InMemoryEllaiUsageStore();
        var receipts = new EllaiReceiptStore();
        if (toolName == EllaiToolNames.Receipts)
            await receipts.RecordAsync(session.SessionId, "action", "idempotency", "plan", 1, null, null,
                DateTimeOffset.UtcNow, TimeSpan.FromHours(1), "policy", default);
        using var model = new FrameworkStreamingClient(toolName, filterGoal);
        var registry = new EllaiUiRegistry();
        if (!changeCatalogue) EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var uiObservations = new InMemoryEllaiUiObservationStore();
        await uiObservations.RecordAsync(new(session.SessionId, "view-1", "context-1", "web", ["report.entry"],
            DateTimeOffset.UtcNow.AddSeconds(-2), [new("report.entry", UiObservationState.Ready)]), CancellationToken.None);
        if (changeObservationBeforeAnswer)
            model.BeforeAnswer = () => uiObservations.RecordAsync(new(session.SessionId, "view-2", "context-2", "web", [],
                DateTimeOffset.UtcNow), CancellationToken.None);
        else if (toolName == EllaiToolNames.UiObserve)
            model.BeforeAnswer = () => uiObservations.RecordAsync(new(session.SessionId, "view-1", "context-1", "web", ["report.entry"],
                DateTimeOffset.UtcNow, [new("report.entry", UiObservationState.Ready)]), CancellationToken.None);
        var registrations = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<EllaiUsageLedger>()
            .AddSingleton<IEllaiUsageStore>(usage).AddScoped(_ => new EllaiPromptBuilder("Synthetic test policy", "v-test"))
            .AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent, receipts: receipts, uiRegistry: registry,
                uiDiscovery: new EllaiUiDiscoveryService(), uiObservations: uiObservations));
        registrations.AddEllaiReasoningExecutor(new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture"
        }, "framework");
        registrations.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, model);
        await using var services = registrations.BuildServiceProvider();
        using (var scope = services.CreateScope())
            Assert.IsType<EllaiFrameworkExecutor>(scope.ServiceProvider.GetRequiredService<IEllaiReasoningExecutor>());
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
            if (changeObservationBeforeAnswer)
            {
                Assert.Contains("\"allowed\":true", model.ToolResult);
                Assert.Equal("TerminalFailure", turn.Outcome);
                Assert.Null(turn.AssistantText);
                Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                    item => item.Text.Contains("Synthetic answer.", StringComparison.Ordinal));
                return;
            }
            Assert.Equal("Completed", turn.Outcome);
            Assert.Equal("Synthetic answer.", turn.AssistantText);
            var published = responses.ReadSince(session.SessionId, 0).Responses;
            Assert.Contains(published, item => !item.IsFinal && item.Text.Contains("Synthetic answer."));
            Assert.Contains(published, item => item.IsReconciliation && item.Text == "Synthetic answer.");
            Assert.Equal(toolName is null ? 1 : 2, model.Calls);
            Assert.Equal(toolName is null ? 1 : 2, (await usage.TotalsAsync(session.SessionId, CancellationToken.None)).Calls);
            if (toolName is not null)
            {
                Assert.Contains("\"allowed\":true", model.ToolResult);
                Assert.Contains(published, item => item.Progress?.State == "Acknowledgement");
            }
            if (toolName is not null)
            {
                Assert.True(EllaiToolTrace.TryRead(turn.ToolTraceJson!, out var trace));
                Assert.Contains(trace!.Observations, item => item.ToolName == toolName &&
                    (toolName == EllaiToolNames.Now ? item.ClockVersion == EllaiObservedToolResult.CurrentClockVersion :
                     toolName == EllaiToolNames.Capabilities ? item.CapabilityVersion == EllaiObservedToolResult.CurrentCapabilityVersion :
                     toolName == EllaiToolNames.Receipts ? item.ReceiptListDigest is not null :
                     toolName == EllaiToolNames.UiObserve ? item.UiObservationReplayScope is not null :
                        item.UiCatalogueReplayScope is not null && (!filterGoal || item.UiCatalogueReplayScope.GoalTag == "report-help")));
            }
            {
                await worker.StopAsync(timeout.Token);
                if (changeCatalogue) EllaiReportGuidanceDescriptors.RegisterAll(registry);
                using var resumedWorker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
                await resumedWorker.StartAsync(timeout.Token);
                try
                {
                    var current = await store.GetAsync(session.SessionId, timeout.Token);
                    Assert.NotNull(current);
                    var next = work with { Utterance = "Follow-up question", ClientUtteranceId = "framework-u2", CycleId = "cycle-2" };
                    Assert.NotNull(await store.TryAcceptUtteranceAsync(current with
                        { SessionVersion = current.SessionVersion + 1, CyclesUsed = current.CyclesUsed + 1 }, current.SessionVersion,
                        next.ClientUtteranceId, timeout.Token, EllaiUtteranceWorkEnvelope.Serialize(next, next.ClientUtteranceId)));
                    EllaiConversationTurn nextTurn;
                    do
                    {
                        await Task.Delay(10, timeout.Token);
                        nextTurn = (await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token))
                            .Single(item => item.ClientUtteranceId == next.ClientUtteranceId);
                    } while (nextTurn.CompletedAtUtc is null);
                    Assert.Equal("Completed", nextTurn.Outcome);
                    Assert.Equal(!changeCatalogue, model.LastMessages.Contains("Synthetic question"));
                    Assert.Equal(!changeCatalogue, model.LastMessages.Contains("Synthetic answer."));
                    Assert.Contains("Follow-up question", model.LastMessages);
                }
                finally { await resumedWorker.StopAsync(CancellationToken.None); }
            }
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class FrameworkStreamingClient(string? toolName, bool filterGoal) : IChatClient
    {
        public int Calls { get; private set; }
        public string ToolResult { get; private set; } = string.Empty;
        public Func<Task>? BeforeAnswer { get; set; }
        public IReadOnlyList<string> LastMessages { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastMessages = messages.Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
                .Select(message => message.Text).ToArray();
            if (toolName is not null && Calls == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", toolName, filterGoal
                        ? new Dictionary<string, object?> { ["goal"] = "report-help" }
                        : toolName == EllaiToolNames.OwnedCaseCount ? new Dictionary<string, object?> { ["window_days"] = "30" }
                        : toolName == EllaiToolNames.State ? new Dictionary<string, object?> { ["projection"] = "queue" }
                        : toolName == EllaiToolNames.Imports ? new Dictionary<string, object?> { ["workspace_id"] = "test", ["organization_id"] = "org", ["country"] = "SZ" }
                        : toolName == EllaiToolNames.ImportStatus ? new Dictionary<string, object?> { ["import_id"] = "b6e9999d-f6d8-4d2d-acd8-dcfd820249f2" }
                        : toolName == EllaiToolNames.UiObserve ? new Dictionary<string, object?> { ["controls"] = "report.entry", ["view"] = "current" }
                        : new Dictionary<string, object?>())]);
            else
            {
                ToolResult = string.Join(" ", messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Select(content => content.Result?.ToString()));
                if (BeforeAnswer is not null) await BeforeAnswer();
                yield return new ChatResponseUpdate(ChatRole.Assistant, Answer);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 4 })]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

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

    [Fact]
    public async Task WorkerKeepsCustomBrokerQueueWhenContainerRegistersAnotherQueue()
    {
        var store = new InMemoryEllaiSessionStore();
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "custom-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("custom-queue-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Show my cases", "cases", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.State },
            EllaiConsentPurposes.PublicProductHelp, "custom-u");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        var brokerQueue = new CountingQueue();
        var containerQueue = new CountingQueue();
        using var model = new FrameworkStreamingClient(EllaiToolNames.State, false);
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            new EllaiToolBroker(new EllaiPolicyService(), consent, queue: brokerQueue),
            new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<IEllaiReasoningExecutor>(executor)
            .AddSingleton<ICaseQueueQuery>(containerQueue)
            .AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent, queue: brokerQueue)).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(20, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("Completed", turn.Outcome);
            Assert.True(brokerQueue.Calls >= 2); // Original read plus current-authority recheck.
            Assert.Equal(0, containerQueue.Calls);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class CountingQueue : ICaseQueueQuery
    {
        public int Calls { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));
        }
    }

    [Theory]
    [InlineData(EllaiToolNames.State)]
    [InlineData(EllaiToolNames.OwnedCaseCount)]
    [InlineData(EllaiToolNames.Receipts)]
    [InlineData(EllaiToolNames.ImportDestinations)]
    [InlineData(EllaiToolNames.Imports)]
    [InlineData(EllaiToolNames.ImportStatus)]
    [InlineData(EllaiToolNames.UiCatalogue)]
    [InlineData(EllaiToolNames.UiObserve)]
    public async Task FrameworkWorkerFinishesTimedOutTurnBeforeNoncooperativeReadReturns(string toolName)
    {
        var store = new InMemoryEllaiSessionStore();
        var responses = new EllaiSessionResponseStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "timeout-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var session = new EllaiSessionRecord("isolated-tool-session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Count my cases", "cases", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { toolName },
            EllaiConsentPurposes.PublicProductHelp, "timeout-u");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1, work.ClientUtteranceId,
            default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        var queue = new NoncooperativeRead();
        var topics = new InMemoryEllaiTopicStore();
        using var model = new FrameworkStreamingClient(toolName, false);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, receipts: queue, queue: queue, imports: queue, importWorkspaces: queue,
            uiAuthority: new EllaiUiAuthorityResolver(identityAuthority: queue),
            topics: new EllaiTopicService(topics), dashboard: queue);
        var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
            broker, new EllaiUsageLedger(), new InMemoryEllaiUsageStore(), TimeSpan.FromMilliseconds(150));
        await using var services = new ServiceCollection()
            .AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store)
            .AddSingleton(responses).AddSingleton(consent).AddSingleton<IEllaiReasoningExecutor>(executor)
            .AddSingleton<ICaseQueueQuery>(queue)
            .AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent, receipts: queue, queue: queue, imports: queue, importWorkspaces: queue,
                uiAuthority: new EllaiUiAuthorityResolver(identityAuthority: queue),
                topics: new EllaiTopicService(topics), dashboard: queue)).BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(services, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await queue.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(20, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("TerminalFailure", turn.Outcome);
            Assert.Null(turn.AssistantText);
            Assert.False(queue.Release.Task.IsCompleted);

            var current = (await store.GetAsync(session.SessionId, timeout.Token))!;
            var next = work with { Utterance = "Say hello", ClientUtteranceId = "after-timeout", CycleId = "cycle-2",
                AllowedTools = new HashSet<string>() };
            Assert.NotNull(await store.TryAcceptUtteranceAsync(current with
            {
                SessionVersion = current.SessionVersion + 1, CyclesUsed = current.CyclesUsed + 1
            }, current.SessionVersion, next.ClientUtteranceId, timeout.Token,
                EllaiUtteranceWorkEnvelope.Serialize(next, next.ClientUtteranceId)));
            EllaiConversationTurn? following;
            do
            {
                await Task.Delay(20, timeout.Token);
                following = (await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token))
                    .SingleOrDefault(item => item.ClientUtteranceId == next.ClientUtteranceId);
            } while (following?.CompletedAtUtc is null);
            Assert.Equal("Completed", following.Outcome);
            Assert.Equal("Synthetic answer.", following.AssistantText);
            Assert.False(queue.Release.Task.IsCompleted);

            queue.Release.TrySetResult();
            await queue.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(await topics.ListAsync(session.SessionId, CancellationToken.None));
            Assert.DoesNotContain(responses.ReadSince(session.SessionId, 0).Responses,
                item => item.ClientUtteranceId == work.ClientUtteranceId &&
                    item.Text.Contains("Synthetic answer.", StringComparison.Ordinal));
        }
        finally
        {
            queue.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class NoncooperativeRead : IDashboardAggregateQuery, ICaseQueueQuery, IEllaiApprovalStore, ICaseImportStore, ICaseImportWorkspaceReader, IEllaiUiIdentityAuthority
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> IsCurrentAsync(ActorContext actor, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task;
            Finished.TrySetResult();
            return true;
        }
        public async Task<DashboardAggregateResult> GetAsync(ActorContext actor, TimeSpan window, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task;
            Finished.TrySetResult();
            return new(DashboardAggregateAccess.Allowed, [], DateTimeOffset.UtcNow, false);
        }
        public async Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task;
            Finished.TrySetResult();
            return new(CaseQueueAccess.Allowed, []);
        }
        public async Task<IReadOnlyList<EllaiReceipt>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            Finished.TrySetResult();
            return [];
        }
        public Task<EllaiReceipt?> GetAsync(string receiptId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EllaiReceipt> RecordAsync(string sessionId, string actionId, string idempotencyKey, string planHash,
            int planVersion, string? approvedRecipient, DateTimeOffset? approvedDueAtUtc, DateTimeOffset now,
            TimeSpan approvalLifetime, string policyHash, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EllaiReceipt?> MarkStatusAsync(string receiptId, EllaiReceiptStatus status, DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<IReadOnlyList<CaseImportWorkspace>> GetWorkspacesAsync(ActorContext actor, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task;
            Finished.TrySetResult();
            return [];
        }
        public async Task<IReadOnlyList<CaseImportHistoryItem>> GetRecentAsync(ActorContext actor, string workspaceId,
            string countryCode, CancellationToken ct, string? organizationId = null)
        {
            Started.TrySetResult();
            await Release.Task;
            Finished.TrySetResult();
            return [];
        }
        public Task<IReadOnlyList<CaseImportRowOutcome>> GetIssuesAsync(ActorContext actor, Guid importId, long afterRow, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseImportLease> OpenAsync(ActorContext actor, Guid importId, CaseImportContext context, long startRow, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> BeginRowAsync(CaseImportLease lease, long row, Guid? commandId, byte[] sourceHash, byte[] sourceBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteRowAsync(CaseImportLease lease, CaseImportRowOutcome result, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseAsync(CaseImportLease lease, string state, string? errorCode, CancellationToken ct) => throw new NotSupportedException();
        public async Task<CaseImportSummary?> GetAsync(ActorContext actor, Guid importId, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task;
            Finished.TrySetResult();
            return null;
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
        public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();
        public Task<HttpResponseMessage> SynthesizeAsync(string requestId, string text, CancellationToken cancellationToken)
        {
            Text = text;
            Requests.Enqueue(text);
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
        // Invalid JSON is retried under the configured advisory usage policy. An oversized
        // response exercises the terminal-failure path without relying on a retry ceiling.
        using var client = new OutcomeChatClient(valid ? Answer : new string('x', 512_001), superseded ? async () =>
        {
            await store.TryUpdateAsync(session with { SessionVersion = 3, InteractionEpoch = 2 },
                2, CancellationToken.None);
        } : null);
        var loop = new EllaiFrameworkExecutor(client, new EllaiPromptBuilder("Synthetic test policy", "v-test"),
            new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(sessionTokenBudget: 10_000, enforceLimits: false), new InMemoryEllaiUsageStore());
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
            EllaiSessionResponseDto? final;
            do
            {
                final = responses.ReadSince(session.SessionId, 0).Responses.LastOrDefault(item => item.IsReconciliation);
                if (final is null) await Task.Delay(10, timeout.Token);
            } while (final is null);
            Assert.True(final.IsReconciliation);
            Assert.Equal(expectedOutcome, final.Outcome);
            Assert.Equal(turn.AssistantText ?? "", final.Text);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class OutcomeChatClient(string response, Func<Task>? beforeResponse = null) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (beforeResponse is not null) await beforeResponse();
            cancellationToken.ThrowIfCancellationRequested();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (beforeResponse is not null) await beforeResponse();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, response);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
