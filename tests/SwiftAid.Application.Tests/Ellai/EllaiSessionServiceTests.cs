using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T04")]
public sealed class EllaiSessionServiceTests
{
    [Fact]
    public async Task CrashAfterAcceptedTurnRestoresExactlyOneCheckpoint()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            CancellationToken.None);

        var accepted = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            CancellationToken.None);

        Assert.NotNull(accepted);

        var checkpoint = await service.SaveCheckpointAsync(
            view.SessionId, "turn-1", new[] { "topic-1" }, Array.Empty<string>(),
            CancellationToken.None);

        Assert.NotNull(checkpoint);

        var replay = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            CancellationToken.None);

        Assert.Null(replay);
    }

    [Fact]
    public async Task StaleFenceCannotAppendCurrentOutput()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            CancellationToken.None);

        var first = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "first", null, "typed", 1),
            CancellationToken.None);

        Assert.NotNull(first);

        var stale = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-2", view.SessionId, view.SessionVersion,
                "stale retry", null, "typed", 1),
            CancellationToken.None);

        Assert.Null(stale);
    }

    [Fact]
    public async Task CancelledSessionCannotSilentlyResume()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            CancellationToken.None);

        Assert.True(await service.CancelAsync(view.SessionId, view.SessionVersion, CancellationToken.None));

        var after = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion + 1,
                "hello?", null, "typed", 2),
            CancellationToken.None);

        Assert.Null(after);
    }

    [Fact]
    public async Task ThirteenthCycleIsDenied()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            CancellationToken.None);

        EllaiSessionView? current = view;
        for (var i = 0; i < EllaiSessionService.DefaultCyclesAllowed; i++)
        {
            var acceptance = await service.SubmitUtteranceAsync(
                new SubmitUtteranceRequest($"u-{i}", view.SessionId, current!.SessionVersion,
                    $"q{i}", null, "typed", i + 1),
                CancellationToken.None);
            Assert.NotNull(acceptance);
            Assert.False(acceptance.Replay);
            current = acceptance.View;
        }

        var over = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-over", view.SessionId, current!.SessionVersion,
                "one more", null, "typed", 99),
            CancellationToken.None);

        Assert.Null(over);
    }

    [Fact]
    public async Task ReplayedUtteranceIdentityReturnsViewWithoutConsumingCycle()
    {
        var service = new EllaiSessionService(new InMemoryEllaiSessionStore());
        var view = await service.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "req-1", "cases", null, "text", "text"),
            CancellationToken.None);

        var first = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, view.SessionVersion,
                "What is overdue?", null, "typed", 1),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.False(first.Replay);
        Assert.Equal(1, first.View.CyclesUsed);

        // Same identity with the current version: idempotent replay.
        var replay = await service.SubmitUtteranceAsync(
            new SubmitUtteranceRequest("u-1", view.SessionId, first.View.SessionVersion,
                "What is overdue?", null, "typed", 1),
            CancellationToken.None);

        Assert.NotNull(replay);
        Assert.True(replay.Replay);
        Assert.Equal(first.View.SessionVersion, replay.View.SessionVersion);
        Assert.Equal(1, replay.View.CyclesUsed);
    }
}
