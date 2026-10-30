using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T13")]
public sealed class EllaiLauncherTests
{
    [Fact]
    public void RequestReachesSubscriberWithChosenMode()
    {
        var launcher = new EllaiLauncher();
        Assert.False(launcher.HasSubscribers);

        EllaiMode? received = null;
        launcher.Requested += mode => received = mode;
        Assert.True(launcher.HasSubscribers);

        launcher.Request(EllaiMode.GuideMe);

        Assert.Equal(EllaiMode.GuideMe, received);
    }

    [Fact]
    public void RequestWithoutSubscribersIsHarmless()
    {
        var launcher = new EllaiLauncher();
        launcher.Request(EllaiMode.Ask);
        Assert.False(launcher.HasSubscribers);
    }
}
