using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiActivityStatusTests
{
    private static EllaiActivityEvent Event(
        string session, long epoch, long sequence, EllaiActivityKind kind,
        string detail = "", string cycle = "cycle-1") =>
        new(session, epoch, sequence, kind, cycle, detail, DateTimeOffset.UtcNow);

    [Fact]
    public void AcceptedBeforeDispatchProjectsCheckingImmediately()
    {
        var coordinator = new EllaiActivityStatusCoordinator();

        var status = coordinator.Publish(Event("s-1", 1, 1, EllaiActivityKind.OperationAccepted));

        Assert.NotNull(status);
        Assert.Equal("Checking that for you…", status.Text);
        Assert.False(status.Terminal);
        Assert.Same(status, coordinator.CurrentStatus("s-1", 1));
    }

    [Fact]
    public void ToolStagesProjectContextualTemplates()
    {
        var coordinator = new EllaiActivityStatusCoordinator();
        coordinator.Publish(Event("s-1", 1, 1, EllaiActivityKind.OperationAccepted));

        var docs = coordinator.Publish(Event("s-1", 1, 2, EllaiActivityKind.ToolStarted, "docs"));
        Assert.Equal("Checking SwiftAid's technical reference…", docs?.Text);

        var read = coordinator.Publish(Event("s-1", 1, 3, EllaiActivityKind.ToolStarted, "read"));
        Assert.Equal("Checking the latest recorded information…", read?.Text);

        var observed = coordinator.Publish(Event("s-1", 1, 4, EllaiActivityKind.ToolObservationAccepted));
        Assert.Equal("Reviewing the evidence…", observed?.Text);

        var approval = coordinator.Publish(Event("s-1", 1, 5, EllaiActivityKind.ApprovalRequired));
        Assert.Equal("Waiting for your approval…", approval?.Text);
        Assert.Equal("approval", approval?.Phase);
    }

    [Fact]
    public void LateDuplicateAndWrongControllerEventsAreFenced()
    {
        var coordinator = new EllaiActivityStatusCoordinator();
        coordinator.Publish(Event("s-1", 1, 3, EllaiActivityKind.ToolStarted, "read"));

        Assert.Null(coordinator.Publish(Event("s-1", 1, 2, EllaiActivityKind.ToolStarted, "docs")));
        Assert.Null(coordinator.Publish(Event("s-1", 1, 3, EllaiActivityKind.ToolStarted, "docs")));
        Assert.Null(coordinator.Publish(Event("s-1", 0, 9, EllaiActivityKind.ToolStarted, "docs")));
        Assert.Null(coordinator.CurrentStatus("s-1", 0));
        Assert.Null(coordinator.CurrentStatus("s-unknown", 1));
    }

    [Fact]
    public void TerminalOutcomesProjectHonestReasons()
    {
        var coordinator = new EllaiActivityStatusCoordinator();

        var done = coordinator.Publish(Event("s-1", 1, 1, EllaiActivityKind.CycleCompleted, "verified"));
        Assert.Equal("Done — the result is verified.", done?.Text);
        Assert.True(done?.Terminal);

        var denied = coordinator.Publish(Event("s-2", 1, 1, EllaiActivityKind.CycleBlocked, "denied"));
        Assert.Equal("I can't check that with the current permissions.", denied?.Text);
        Assert.Equal("blocked", denied?.Phase);
        Assert.True(denied?.Terminal);

        var budget = coordinator.Publish(Event("s-3", 1, 1, EllaiActivityKind.CycleBlocked, "budget"));
        Assert.Equal("The trusted budget for this request is used up.", budget?.Text);
    }

    [Fact]
    public void CancelFencesLateCallbacks()
    {
        var coordinator = new EllaiActivityStatusCoordinator();
        coordinator.Publish(Event("s-1", 1, 1, EllaiActivityKind.ToolStarted, "read"));

        var stopped = coordinator.Publish(Event("s-1", 1, 2, EllaiActivityKind.SessionCancelled));
        Assert.Equal("Stopped.", stopped?.Text);
        Assert.True(stopped?.Terminal);

        Assert.Null(coordinator.Publish(Event("s-1", 1, 3, EllaiActivityKind.ToolObservationAccepted)));
        Assert.Null(coordinator.Publish(Event("s-1", 2, 4, EllaiActivityKind.OperationAccepted)));
    }
}
