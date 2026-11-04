using SwiftAid.Client.Authentication;

namespace SwiftAid.Mobile.Services.Http;

/// <summary>Relays Gateway cookies through an HttpClientFactory transport to
/// the same cookie container used by Mobile password authentication. Cookie-authenticated
/// writes can opt into fetching the Gateway antiforgery token.</summary>
public sealed class CookieSessionRelayHandler : DelegatingHandler
{
    private readonly CookieSessionHttpClient _sessions;
    private readonly NativeSessionHttpOptions _gateway;
    private readonly bool _protectCookieWrites;

    public CookieSessionRelayHandler(
        CookieSessionHttpClient sessions,
        NativeSessionHttpOptions gateway,
        bool protectCookieWrites = false)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _protectCookieWrites = protectCookieWrites;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        if (uri is null || !_gateway.IsWithinConfiguredGateway(uri))
        {
            throw new InvalidOperationException("Authenticated requests must target the configured Gateway.");
        }

        await _sessions.EnsureAuthenticationCookieRestoredAsync(cancellationToken).ConfigureAwait(false);

        if (request.Headers.Authorization is null)
        {
            if (_protectCookieWrites && _sessions.HasAuthenticationCookie &&
                request.Method != HttpMethod.Get && request.Method != HttpMethod.Head &&
                request.Method != HttpMethod.Options && request.Method != HttpMethod.Trace &&
                !request.Headers.Contains("X-CSRF-TOKEN"))
            {
                var token = await _sessions.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                    throw new HttpRequestException("Request verification is unavailable.");
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
            }
            var cookieHeader = _sessions.GetCookieHeader(uri);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookieHeaders))
        {
            _sessions.StoreCookies(uri, cookieHeaders);
            await _sessions.PersistAuthenticationCookieAsync(cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
