using System.Net;
using SwiftAid.Client.Authentication;

namespace SwiftAid.Client.Tests;

public sealed class GatewayAuthCookieSnapshotTests
{
    private static readonly Uri Gateway = new("https://gateway.swiftaid.example/");
    private static readonly Uri DevelopmentGateway = new("http://10.0.2.2:8080/");
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CaptureAcceptsOnlyPersistentSecureHostAuthCookie()
    {
        var cookie = new Cookie(GatewayAuthCookieSnapshot.CookieName, "opaque-auth-value", "/")
        {
            Secure = true,
            HttpOnly = true,
            Expires = Now.AddDays(30).UtcDateTime
        };

        var snapshot = GatewayAuthCookieSnapshot.Capture(Gateway, cookie, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(Gateway.GetLeftPart(UriPartial.Authority), snapshot.GatewayOrigin);
        Assert.Equal("opaque-auth-value", snapshot.Value);
        Assert.Equal(Now.AddDays(30), snapshot.ExpiresAtUtc);
    }

    [Fact]
    public void CaptureRejectsSessionCookiesAndCookiesWithoutSecurityFlags()
    {
        var sessionCookie = new Cookie(GatewayAuthCookieSnapshot.CookieName, "opaque-auth-value", "/")
        {
            Secure = true,
            HttpOnly = true
        };
        var insecureCookie = new Cookie(GatewayAuthCookieSnapshot.CookieName, "opaque-auth-value", "/")
        {
            HttpOnly = true,
            Expires = Now.AddDays(30).UtcDateTime
        };

        Assert.Null(GatewayAuthCookieSnapshot.Capture(Gateway, sessionCookie, Now));
        Assert.Null(GatewayAuthCookieSnapshot.Capture(Gateway, insecureCookie, Now));
    }

    [Fact]
    public void RestoreRequiresSameHttpsGatewayAndUnexpiredCookie()
    {
        var snapshot = new GatewayAuthCookieSnapshot(
            Gateway.GetLeftPart(UriPartial.Authority),
            "opaque-auth-value",
            Now.AddDays(30));

        Assert.Null(snapshot.RestoreFor(new Uri("https://other.swiftaid.example/"), Now));
        Assert.Null(snapshot.RestoreFor(new Uri("http://gateway.swiftaid.example/"), Now));
        Assert.Null(snapshot.RestoreFor(Gateway, Now.AddDays(30)));
    }

    [Fact]
    public void RestoreRecreatesHostOnlySecureHttpOnlyCookie()
    {
        var expires = Now.AddDays(30);
        var snapshot = new GatewayAuthCookieSnapshot(
            Gateway.GetLeftPart(UriPartial.Authority),
            "opaque-auth-value",
            expires);

        var cookie = snapshot.RestoreFor(Gateway, Now);

        Assert.NotNull(cookie);
        Assert.Equal(GatewayAuthCookieSnapshot.CookieName, cookie.Name);
        Assert.Equal("opaque-auth-value", cookie.Value);
        Assert.Equal("/", cookie.Path);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Empty(cookie.Domain);
        Assert.Equal(expires.UtcDateTime, cookie.Expires);
    }

    [Fact]
    public void DevelopmentCookiePersistsOnlyForLoopbackAndAndroidEmulatorGateway()
    {
        var expires = Now.AddDays(30);
        var cookie = new Cookie(GatewayAuthCookieSnapshot.DevelopmentCookieName, "local-dev-cookie", "/")
        {
            HttpOnly = true,
            Expires = expires.UtcDateTime
        };

        var snapshot = GatewayAuthCookieSnapshot.Capture(DevelopmentGateway, cookie, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(GatewayAuthCookieSnapshot.DevelopmentCookieName, snapshot.AuthCookieName);
        Assert.False(snapshot.IsSecure);
        Assert.Equal("local-dev-cookie", snapshot.Value);
        Assert.Equal(expires, snapshot.ExpiresAtUtc);
        Assert.True(GatewayAuthCookieSnapshot.SupportsPersistentSession(DevelopmentGateway));
        Assert.True(GatewayAuthCookieSnapshot.SupportsPersistentSession(new Uri("http://127.0.0.1:8080/")));
        Assert.False(GatewayAuthCookieSnapshot.SupportsPersistentSession(new Uri("http://gateway.swiftaid.example/")));
    }

    [Fact]
    public void DevelopmentCookieRestoreRemainsHostBoundAndExpires()
    {
        var snapshot = new GatewayAuthCookieSnapshot(
            DevelopmentGateway.GetLeftPart(UriPartial.Authority),
            "local-dev-cookie",
            Now.AddDays(30))
        {
            AuthCookieName = GatewayAuthCookieSnapshot.DevelopmentCookieName,
            IsSecure = false
        };

        var cookie = snapshot.RestoreFor(DevelopmentGateway, Now);

        Assert.NotNull(cookie);
        Assert.Equal(GatewayAuthCookieSnapshot.DevelopmentCookieName, cookie.Name);
        Assert.False(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Empty(cookie.Domain);
        Assert.Null(snapshot.RestoreFor(new Uri("http://10.0.2.3:8080/"), Now));
        Assert.Null(snapshot.RestoreFor(DevelopmentGateway, Now.AddDays(30)));
    }
}
