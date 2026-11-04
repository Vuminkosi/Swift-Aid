using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed class SharedEllaiGuidanceControlTests
{
    [Fact]
    public async Task Journey_can_start_pause_resume_and_stop_on_either_host()
    {
        var control = new SharedEllaiGuidanceControl();
        var started = await control.StartAsync("cases", "1", GuidanceMode.Navigation, "overview", default, "start");
        var retry = await control.StartAsync("cases", "1", GuidanceMode.Navigation, "overview", default, "start");
        Assert.Equal(started.Position.GuidanceAttemptId, retry.Position.GuidanceAttemptId);
        var id = started.Position.GuidanceAttemptId;
        var paused = await control.DispatchAsync(id, started.Position.GuidanceVersion, GuidanceCommandKind.Pause,
            GuidanceMode.Navigation, null, null, default, "pause");
        Assert.Equal(GuidanceStatus.Paused, paused!.Status);
        var resumed = await control.DispatchAsync(id, paused.Position.GuidanceVersion, GuidanceCommandKind.Resume,
            GuidanceMode.Navigation, null, null, default, "resume");
        Assert.Equal(GuidanceStatus.Showing, resumed!.Status);
        var stopped = await control.StopAsync(id, resumed.Position.GuidanceVersion, GuidanceMode.Navigation, default, "stop");
        Assert.Equal(GuidanceStatus.Cancelled, stopped!.Status);
        Assert.Null(await control.DispatchAsync(id, stopped.Position.GuidanceVersion, GuidanceCommandKind.Resume,
            GuidanceMode.Navigation, null, null, default, "late-resume"));
    }

    [Fact]
    public async Task Conflicting_start_identity_and_foreign_host_attempt_are_rejected()
    {
        var first = new SharedEllaiGuidanceControl();
        var second = new SharedEllaiGuidanceControl();
        var started = await first.StartAsync("cases", "1", GuidanceMode.Navigation, null, default, "start");
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.StartAsync("other", "1",
            GuidanceMode.Navigation, null, default, "start"));
        Assert.Null(await second.StopAsync(started.Position.GuidanceAttemptId, 1, GuidanceMode.Navigation, default));
    }
}
