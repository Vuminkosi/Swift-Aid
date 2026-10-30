using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T13")]
public sealed class EllaiSurfaceLogicTests
{
    [Fact]
    public void PositionResolveClampsInsideViewport()
    {
        var inside = EllaiComposerPosition.Resolve(new EllaiComposerPosition(100, 100), 1280, 800);
        Assert.Equal(100, inside.X);
        Assert.Equal(100, inside.Y);

        var outside = EllaiComposerPosition.Resolve(new EllaiComposerPosition(-50, 2000), 1280, 800);
        Assert.Equal(0, outside.X);
        Assert.Equal(640, outside.Y);

        var tiny = EllaiComposerPosition.Resolve(new EllaiComposerPosition(500, 500), 100, 100);
        Assert.Equal(0, tiny.X);
        Assert.Equal(0, tiny.Y);
    }

    [Fact]
    public void PositionMoveAndResetBehave()
    {
        var moved = EllaiComposerPosition.Default.Move(24, -24);
        Assert.Equal(EllaiComposerPosition.Default.X + 24, moved.X);

        var reset = EllaiComposerPosition.Default;
        Assert.Equal(24, reset.X);
        Assert.Equal(24, reset.Y);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Discovering")]
    [InlineData("Planning")]
    [InlineData("NeedsInput")]
    [InlineData("Ready")]
    [InlineData("Active")]
    [InlineData("Replanning")]
    [InlineData("Blocked")]
    public void KnownPhasesMapToLabels(string phase)
    {
        Assert.Equal(phase, EllaiStatusPhases.Label(phase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chatting")]
    public void UnknownPhasesRenderNothing(string? phase)
    {
        Assert.Null(EllaiStatusPhases.Label(phase));
    }
}
