using System.Net;

namespace SwiftAid.Client.Authentication;

/// <summary>A bounded representation of a persistent Gateway authentication cookie.</summary>
public sealed record GatewayAuthCookieSnapshot(
    string GatewayOrigin,
    string Value,
    DateTimeOffset ExpiresAtUtc)
{
    public const string CookieName = "__Host-SwiftAid.Auth";
    public const string DevelopmentCookieName = "SwiftAid.Auth.Dev";

    /// <summary>
    /// Local HTTPS is always eligible. Plain HTTP persistence is limited to
    /// loopback and the Android emulator host alias used by the Debug app.
    /// </summary>
    public static bool SupportsPersistentSession(Uri gateway) =>
        gateway.IsAbsoluteUri &&
        (string.Equals(gateway.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         IsDevelopmentHttpGateway(gateway));

    private static bool IsDevelopmentHttpGateway(Uri gateway) =>
        string.Equals(gateway.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        (gateway.IsLoopback ||
         string.Equals(gateway.IdnHost, "10.0.2.2", StringComparison.OrdinalIgnoreCase));

    /// <summary>The exact cookie scheme captured for backwards-compatible restore.</summary>
    public string AuthCookieName { get; init; } = CookieName;

    /// <summary>Whether the captured cookie carries the Secure transport flag.</summary>
    public bool IsSecure { get; init; } = true;

    public static GatewayAuthCookieSnapshot? Capture(
        Uri gateway,
        Cookie? cookie,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var isSecureHttpsCookie = gateway.IsAbsoluteUri &&
            string.Equals(gateway.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            cookie is { Secure: true } &&
            string.Equals(cookie.Name, CookieName, StringComparison.Ordinal);
        var isLoopbackDevelopmentCookie = gateway.IsAbsoluteUri &&
            IsDevelopmentHttpGateway(gateway) &&
            cookie is { Secure: false } &&
            string.Equals(cookie.Name, DevelopmentCookieName, StringComparison.Ordinal);
        if (!gateway.IsAbsoluteUri ||
            (!isSecureHttpsCookie && !isLoopbackDevelopmentCookie) ||
            cookie is null ||
            string.IsNullOrWhiteSpace(cookie.Value) ||
            cookie.Value.Length > 4096 ||
            cookie.Value.Any(char.IsControl) ||
            !cookie.HttpOnly ||
            !string.Equals(cookie.Path, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(cookie.Domain) ||
            cookie.Expires == DateTime.MinValue)
        {
            return null;
        }

        var expiresAtUtc = new DateTimeOffset(cookie.Expires.ToUniversalTime());
        if (expiresAtUtc <= now)
        {
            return null;
        }

        return new GatewayAuthCookieSnapshot(
            gateway.GetLeftPart(UriPartial.Authority),
            cookie.Value,
            expiresAtUtc)
        {
            AuthCookieName = cookie.Name,
            IsSecure = cookie.Secure
        };
    }

    public Cookie? RestoreFor(Uri gateway, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var isSecureHttpsCookie = gateway.IsAbsoluteUri &&
            string.Equals(gateway.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            IsSecure &&
            string.Equals(AuthCookieName, CookieName, StringComparison.Ordinal);
        var isLoopbackDevelopmentCookie = gateway.IsAbsoluteUri &&
            IsDevelopmentHttpGateway(gateway) &&
            !IsSecure &&
            string.Equals(AuthCookieName, DevelopmentCookieName, StringComparison.Ordinal);
        if (!gateway.IsAbsoluteUri ||
            (!isSecureHttpsCookie && !isLoopbackDevelopmentCookie) ||
            string.IsNullOrWhiteSpace(GatewayOrigin) ||
            string.IsNullOrWhiteSpace(Value) ||
            !string.Equals(GatewayOrigin, gateway.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
            Value.Length > 4096 ||
            Value.Any(char.IsControl) ||
            ExpiresAtUtc <= now)
        {
            return null;
        }

        return new Cookie(AuthCookieName, Value, "/")
        {
            Secure = IsSecure,
            HttpOnly = true,
            Expires = ExpiresAtUtc.UtcDateTime
        };
    }
}
