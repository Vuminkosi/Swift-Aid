using SwiftAid.Client.Authentication;

namespace SwiftAid.Client.Tests;

public sealed class NativeSessionHttpOptionsTests
{
    [Fact]
    public void HttpIsRejectedUnlessDevelopmentOverrideIsExplicit()
    {
        Assert.Throws<ArgumentException>(() =>
            new NativeSessionHttpOptions(new Uri("http://10.0.2.2:8080")));
    }

    [Fact]
    public void DevelopmentOverrideAllowsOnlySameHttpGatewayOrigin()
    {
        var options = new NativeSessionHttpOptions(
            new Uri("http://10.0.2.2:8080"), allowInsecureTransport: true);

        Assert.True(options.IsWithinConfiguredGateway(new Uri("http://10.0.2.2:8080/api/v1/cases")));
        Assert.False(options.IsWithinConfiguredGateway(new Uri("http://example.com/api/v1/cases")));
        Assert.False(options.IsWithinConfiguredGateway(new Uri("https://10.0.2.2:8080/api/v1/cases")));
    }

    [Fact]
    public void HttpsGatewayRemainsRestrictedToHttps()
    {
        var options = new NativeSessionHttpOptions(new Uri("https://gateway.example"));

        Assert.True(options.IsWithinConfiguredGateway(new Uri("https://gateway.example/api/v1/cases")));
        Assert.False(options.IsWithinConfiguredGateway(new Uri("http://gateway.example/api/v1/cases")));
    }
}
