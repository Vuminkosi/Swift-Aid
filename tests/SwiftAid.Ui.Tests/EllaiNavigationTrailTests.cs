using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiNavigationTrailTests
{
    [Fact]
    public void BackReturnsPreviousRegisteredPageWithoutQueriesAndConsumesTheEntry()
    {
        var trail = new EllaiNavigationTrail();
        trail.Record("dashboard?private=value");
        trail.Record("monitoring#private");
        trail.Record("monitoring");
        Assert.Equal("/dashboard", trail.Previous);
        trail.Record("dashboard");
        Assert.Null(trail.Previous);
    }
    [Theory]
    [InlineData("https://example.com/dashboard")]
    [InlineData("../dashboard")]
    [InlineData("/unknown")]
    [InlineData("/case/00000000-0000-0000-0000-000000000000")]
    public void UnknownOrExternalLocationsClearTheTrail(string target)
    {
        var trail = new EllaiNavigationTrail();
        trail.Record("dashboard"); trail.Record("monitoring"); trail.Record(target);
        Assert.Null(trail.Previous);
        Assert.Null(EllaiNavigationTrail.CanonicalRoute(target));
    }
    [Fact]
    public void CanonicalCaseIsRetainedButTrailIsBounded()
    {
        var trail = new EllaiNavigationTrail();
        for (var i = 0; i < 30; i++) trail.Record("case/" + Guid.NewGuid().ToString("D"));
        var steps = 0;
        while (trail.Previous is { } previous) { trail.Record(previous); steps++; }
        Assert.Equal(19, steps);
    }
}
