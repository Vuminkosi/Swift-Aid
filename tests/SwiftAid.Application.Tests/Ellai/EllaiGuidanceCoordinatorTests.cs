using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T27")]
public sealed class EllaiGuidanceCoordinatorTests
{
    [Fact]
    public void StaleCommandVersionYieldsStaleNotExecution()
    {
        var coordinator = new EllaiGuidanceSessionCoordinator();
        var started = coordinator.Start(new StartGuidanceRequest(
            "cmd-1", "journey-1", "1.0", GuidanceMode.Presentation, null));

        var moved = coordinator.Dispatch(new GuidanceControlRequest(
            "cmd-2", started.Position.GuidanceAttemptId,
            started.Position.GuidanceVersion, GuidanceCommandKind.Next, null, null),
            GuidanceMode.Presentation);

        Assert.NotNull(moved);
        Assert.Equal(started.Position.GuidanceVersion + 1, moved.Position.GuidanceVersion);

        var stale = coordinator.Dispatch(new GuidanceControlRequest(
            "cmd-3", started.Position.GuidanceAttemptId,
            started.Position.GuidanceVersion, GuidanceCommandKind.Next, null, null),
            GuidanceMode.Presentation);

        Assert.NotNull(stale);
        Assert.Equal(GuidanceStatus.Stale, stale.Status);
    }

    [Fact]
    public void CancelledAttemptRejectsAllCommands()
    {
        var coordinator = new EllaiGuidanceSessionCoordinator();
        var started = coordinator.Start(new StartGuidanceRequest(
            "cmd-1", "journey-1", "1.0", GuidanceMode.Demo, null));

        var stopped = coordinator.Dispatch(new GuidanceControlRequest(
            "cmd-2", started.Position.GuidanceAttemptId,
            started.Position.GuidanceVersion, GuidanceCommandKind.Stop, null, null),
            GuidanceMode.Demo);

        Assert.NotNull(stopped);
        Assert.Equal(GuidanceStatus.Cancelled, stopped.Status);

        var after = coordinator.Dispatch(new GuidanceControlRequest(
            "cmd-3", started.Position.GuidanceAttemptId,
            stopped.Position.GuidanceVersion, GuidanceCommandKind.Resume, null, null),
            GuidanceMode.Demo);

        Assert.Null(after);
    }

    [Fact]
    public void RetriedCommandIdReturnsOneStableSnapshot()
    {
        var coordinator = new EllaiGuidanceSessionCoordinator();
        var started = coordinator.Start(new StartGuidanceRequest(
            "cmd-1", "journey-1", "1.0", GuidanceMode.Presentation, null));

        var first = coordinator.Dispatch(new GuidanceControlRequest(
            "cmd-retry-1", started.Position.GuidanceAttemptId,
            started.Position.GuidanceVersion, GuidanceCommandKind.Next, null, null),
            GuidanceMode.Presentation);
        var retry = coordinator.Dispatch(new GuidanceControlRequest(
            "cmd-retry-1", started.Position.GuidanceAttemptId,
            started.Position.GuidanceVersion, GuidanceCommandKind.Next, null, null),
            GuidanceMode.Presentation);

        Assert.NotNull(first);
        Assert.NotNull(retry);
        Assert.Equal(first.Position.GuidanceVersion, retry.Position.GuidanceVersion);
        Assert.Equal(first.LastAcceptedCommandSequence, retry.LastAcceptedCommandSequence);
    }

    [Fact]
    public void RevisionActivationFencesSpeechAndStartsNewSegment()
    {
        var coordinator = new EllaiGuidanceSessionCoordinator();
        var started = coordinator.Start(new StartGuidanceRequest(
            "cmd-1", "journey-1", "1.0", GuidanceMode.Navigation, null));

        var plan = new ValidatedGuidancePlan(
            "plan-1", 1, null, "Initial acceptance",
            new[] { "s1" }, Array.Empty<string>(), Array.Empty<string>(),
            new[]
            {
                new GuidancePlanStep("s1", "cases.queue", "o1", PlanStepBehavior.Explain,
                    null, null, Array.Empty<string>(), Array.Empty<string>(), "See the queue.", null)
            },
            Array.Empty<string>(), "ellai-catalogue/1.0", "ellai-uicontract/1.0");

        var revised = coordinator.ApplyRevision(new ActivateGuidanceRevisionRequest(
            "cmd-2", started.Position.GuidanceAttemptId,
            started.Position.GuidanceVersion, plan),
            GuidanceMode.Navigation);

        Assert.NotNull(revised);
        Assert.Equal("plan-1", revised.Position.GeneratedPlanId);
        Assert.Equal(1, revised.Position.GeneratedPlanRevision);
        Assert.Equal(started.Position.SpeechEpoch + 1, revised.Position.SpeechEpoch);
        Assert.Equal("s1", revised.Position.StepId);
    }
}
