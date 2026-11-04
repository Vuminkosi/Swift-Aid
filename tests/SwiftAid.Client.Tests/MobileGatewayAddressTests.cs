using SwiftAid.Client.Mobile;

namespace SwiftAid.Client.Tests;

public sealed class MobileGatewayAddressTests
{
    [Fact]
    public void Resolve_ReturnsNullWhenUnconfigured()
    {
        Assert.Null(MobileGatewayAddress.Resolve(null, "SwiftAid:GatewayBaseAddress", rewriteLoopbackForAndroidEmulator: true));
        Assert.Null(MobileGatewayAddress.Resolve("  ", "SwiftAid:GatewayBaseAddress", rewriteLoopbackForAndroidEmulator: false));
    }

    [Theory]
    [InlineData("http://10.0.2.2:7215")]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com/?q=1")]
    [InlineData("https://example.com/#frag")]
    [InlineData("not-a-uri")]
    [InlineData("/relative/path")]
    public void Resolve_RejectsNonBareHttpsUrls(string configured)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MobileGatewayAddress.Resolve(configured, "SwiftAid:GatewayBaseAddress", rewriteLoopbackForAndroidEmulator: true));
        Assert.Contains("SwiftAid:GatewayBaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AllowsHttpOnlyWhenDevelopmentGatewayIsExplicitlyEnabled()
    {
        var resolved = MobileGatewayAddress.Resolve(
            "http://10.0.2.2:8080",
            "SwiftAid:GatewayBaseAddress",
            rewriteLoopbackForAndroidEmulator: false,
            allowHttpForDevelopmentGateway: true);

        Assert.Equal("http://10.0.2.2:8080/", resolved?.ToString());
    }

    [Fact]
    public void Resolve_RejectsHttpWhenDevelopmentGatewayIsNotEnabled()
    {
        Assert.Throws<InvalidOperationException>(() => MobileGatewayAddress.Resolve(
            "http://10.0.2.2:8080",
            "SwiftAid:GatewayBaseAddress",
            rewriteLoopbackForAndroidEmulator: false));
    }

    [Fact]
    public void Resolve_RejectsBlankSettingName()
    {
        Assert.Throws<ArgumentException>(() =>
            MobileGatewayAddress.Resolve("https://example.com", "  ", rewriteLoopbackForAndroidEmulator: false));
    }

    [Theory]
    [InlineData("https://localhost:7215", "https://10.0.2.2:7215/")]
    [InlineData("https://127.0.0.1:7215/api/", "https://10.0.2.2:7215/api/")]
    [InlineData("https://[::1]:7215", "https://10.0.2.2:7215/")]
    [InlineData("https://LOCALHOST", "https://10.0.2.2/")]
    public void Resolve_RewritesLoopbackToEmulatorAlias(string configured, string expected)
    {
        var resolved = MobileGatewayAddress.Resolve(configured, "SwiftAid:GatewayBaseAddress", rewriteLoopbackForAndroidEmulator: true);
        Assert.Equal(expected, resolved?.ToString());
    }

    [Theory]
    [InlineData("https://localhost:7215")]
    [InlineData("https://swiftaid.online")]
    public void Resolve_LeavesAddressUntouchedWithoutEmulatorRewrite(string configured)
    {
        var resolved = MobileGatewayAddress.Resolve(configured, "SwiftAid:GatewayBaseAddress", rewriteLoopbackForAndroidEmulator: false);
        Assert.Equal(configured, resolved?.ToString().TrimEnd('/'));
    }

    [Fact]
    public void Resolve_LeavesPublicAddressUntouchedOnEmulator()
    {
        var resolved = MobileGatewayAddress.Resolve("https://swiftaid.online", "SwiftAid:GatewayBaseAddress", rewriteLoopbackForAndroidEmulator: true);
        Assert.Equal("https://swiftaid.online/", resolved?.ToString());
    }

    [Theory]
    [InlineData("https://localhost:7215", true)]
    [InlineData("https://127.0.0.1", true)]
    [InlineData("https://[::1]/", true)]
    [InlineData("https://swiftaid.online", false)]
    [InlineData(null, false)]
    [InlineData("not-a-uri", false)]
    public void IsLoopbackConfigured_DetectsLoopbackHosts(string? configured, bool expected)
    {
        Assert.Equal(expected, MobileGatewayAddress.IsLoopbackConfigured(configured));
    }
}
