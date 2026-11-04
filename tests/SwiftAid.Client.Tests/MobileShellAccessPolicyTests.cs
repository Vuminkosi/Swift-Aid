using SwiftAid.Mobile.Services;

namespace SwiftAid.Client.Tests;

public sealed class MobileShellAccessPolicyTests
{
    [Theory]
    [InlineData("/sessions/calendar")]
    [InlineData("/SESSIONS")]
    [InlineData("/about")]
    [InlineData("/privacy")]
    [InlineData("/terms")]
    [InlineData("/report")]
    [InlineData("/dashboard")]
    [InlineData("/sync")]
    [InlineData("/SYNC")]
    public void PublicJourneysAreAccessibleWithoutSignIn(string path) =>
        Assert.True(MobileShellAccessPolicy.IsPublicPath(path));

    [Theory]
    [InlineData("/case/11111111-1111-1111-1111-111111111111")]
    [InlineData("/sessions/calendar/private")]
    [InlineData("/surveillance")]
    [InlineData("/my-cases")]
    [InlineData("/sync/private")]
    public void PrivateAndUnrecognizedRoutesRemainProtected(string path) =>
        Assert.False(MobileShellAccessPolicy.IsPublicPath(path));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void FullEllaiModesRequireResolvedSignIn(bool resolved, bool signedIn, bool publicOnly) =>
        Assert.Equal(publicOnly, MobileShellAccessPolicy.PublicEllaiOnly(resolved, signedIn));
}
