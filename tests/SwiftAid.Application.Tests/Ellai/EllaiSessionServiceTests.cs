using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Sync;
using System.Security.Claims;
using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T04")]
public sealed class EllaiSessionServiceTests
{
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "test-actor"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
    private static ActorContext Actor => ActorContext.FromValidatedPrincipal(Principal);

    [Fact]
    public async Task StartWithCurrentScopeBindsSessionToTheActorAccessEpoch()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var scope = CreateScope(accessEpoch: 1);

        var session = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "scoped-start", null, null, null, null),
            Principal,
            scope,
            CancellationToken.None);

        Assert.Equal("access-epoch-1", session.ScopeDisplay);
    }

    [Fact]
    public async Task StartWithStaleScopeEpochIsRejected()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "stale-scope", null, null, null, null),
            Principal,
            CreateScope(accessEpoch: 2),
            CancellationToken.None));
    }

    private static ScopeManifest CreateScope(long accessEpoch) => new(
        [new ScopeGrantManifest(
            "group-1",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<Guid>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            false)],
        accessEpoch,
        DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task CrashAfterAcceptedTurnRestoresExactlyOneCheckpoint()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            Principal, CancellationToken.None);

        var accepted = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            Actor, CancellationToken.None);

        Assert.NotNull(accepted);

        var checkpoint = await service.SaveCheckpointAsync(
            view.SessionId, "turn-1", new[] { "topic-1" }, Array.Empty<string>(),
            CancellationToken.None);

        Assert.NotNull(checkpoint);

        var replay = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            Actor, CancellationToken.None);

        Assert.NotNull(replay);
        Assert.True(replay.Replay);
    }

    [Fact]
    public async Task StaleFenceCannotAppendCurrentOutput()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            Principal, CancellationToken.None);

        var first = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "first", null, "typed", 1),
            Actor, CancellationToken.None);

        Assert.NotNull(first);

        var stale = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-2", view.SessionId, view.SessionVersion,
                "stale retry", null, "typed", 1),
            Actor, CancellationToken.None);

        Assert.Null(stale);
    }

    [Fact]
    public async Task CancelledSessionCannotSilentlyResume()
    {
        var store = new InMemoryEllaiSessionStore();
        var service = new EllaiSessionService(store);
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            Principal, CancellationToken.None);

        var before = await store.GetAsync(view.SessionId, CancellationToken.None);
        Assert.True(await service.CancelAsync(view.SessionId, view.SessionVersion, Principal, CancellationToken.None));
        var cancelledRecord = await store.GetAsync(view.SessionId, CancellationToken.None);
        Assert.Equal(before!.SessionVersion + 1, cancelledRecord!.SessionVersion);
        Assert.Equal(before.ControlFence + 1, cancelledRecord.ControlFence);
        Assert.Equal(before.SpeechEpoch + 1, cancelledRecord.SpeechEpoch);
        Assert.Equal("Cancelled", cancelledRecord.Status);

        var after = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion + 1,
                "hello?", null, "typed", 2),
            Actor, CancellationToken.None);

        Assert.Null(after);
    }

    [Fact]
    public async Task ThirteenthCycleIsDenied()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            Principal, CancellationToken.None);

        EllaiSessionView? current = view;
        for (var i = 0; i < EllaiSessionService.DefaultCyclesAllowed; i++)
        {
            var acceptance = await service.SubmitUtteranceAsync(
                new SubmitUtteranceRequest($"u-{i}", view.SessionId, current!.SessionVersion,
                    $"q{i}", null, "typed", i + 1),
                Actor, CancellationToken.None);
            Assert.NotNull(acceptance);
            Assert.False(acceptance.Replay);
            current = acceptance.View;
        }

        var over = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-over", view.SessionId, current!.SessionVersion,
                "one more", null, "typed", 99),
            Actor, CancellationToken.None);

        Assert.Null(over);
    }

    [Fact]
    public async Task ReplayedUtteranceIdentityReturnsViewWithoutConsumingCycle()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            Principal, CancellationToken.None);

        var first = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            Actor, CancellationToken.None);

        Assert.NotNull(first);
        Assert.False(first.Replay);
        Assert.Equal(1, first.View.CyclesUsed);

        // Same identity with the current version: idempotent replay.
        var replay = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            Actor, CancellationToken.None);

        Assert.NotNull(replay);
        Assert.True(replay.Replay);
        Assert.Equal(first.View.SessionVersion, replay.View.SessionVersion);
        Assert.Equal(1, replay.View.CyclesUsed);
    }

    [Fact]
    public async Task ConcurrentDistinctUtteranceIdsHaveOneWinnerAndLoserCanRetryAfterRefresh()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-race", null, null, null, null),
            Principal, CancellationToken.None);
        var firstTask = service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("race-a", view.SessionId, view.SessionVersion,
                "first", null, "typed", 1), Actor, CancellationToken.None);
        var secondTask = service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("race-b", view.SessionId, view.SessionVersion,
                "second", null, "typed", 1), Actor, CancellationToken.None);
        var results = await Task.WhenAll(firstTask, secondTask);

        var winner = Assert.Single(results, result => result is not null)!;
        var loserId = results[0] is null ? "race-a" : "race-b";
        var retry = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest(loserId, view.SessionId, winner.View.SessionVersion,
                "retry", null, "typed", 2), Actor, CancellationToken.None);

        Assert.NotNull(retry);
        Assert.False(retry.Replay);
        Assert.Equal(2, retry.View.CyclesUsed);
    }

    [Fact]
    public async Task CompletedSessionCannotBeCancelledOrReopenedAsCancelled()
    {
        var store = new InMemoryEllaiSessionStore();
        var service = new EllaiSessionService(store);
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-completed", null, null, null, null),
            Principal, CancellationToken.None);
        var completed = (await store.GetAsync(view.SessionId, CancellationToken.None))! with
        {
            Status = "Completed",
            SessionVersion = view.SessionVersion + 1,
        };
        await store.TryUpdateAsync(completed, view.SessionVersion, CancellationToken.None);

        Assert.False(await service.CancelAsync(
            view.SessionId, completed.SessionVersion, Principal, CancellationToken.None));
        var after = await store.GetAsync(view.SessionId, CancellationToken.None);
        Assert.Equal("Completed", after!.Status);
        Assert.Equal(completed.SessionVersion, after.SessionVersion);
        Assert.Equal(completed.ControlFence, after.ControlFence);
    }
}
