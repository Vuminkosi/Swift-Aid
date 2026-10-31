using SwiftAid.Client.Authentication;

namespace SwiftAid.Mobile.Services.Http;

/// <summary>Relays Gateway cookies through an HttpClientFactory transport to
/// the same cookie container used by Mobile password authentication.</summary>
public sealed class CookieSessionRelayHandler : DelegatingHandler
{
    private readonly CookieSessionHttpClient _sessions;
    private readonly NativeSessionHttpOptions _gateway;

    public CookieSessionRelayHandler(
        CookieSessionHttpClient sessions,
        NativeSessionHttpOptions gateway)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        if (uri is null || !_gateway.IsWithinConfiguredGateway(uri))
        {
            throw new InvalidOperationException("Ellai requests must target the configured HTTPS Gateway.");
        }

        await _sessions.EnsureAuthenticationCookieRestoredAsync(cancellationToken).ConfigureAwait(false);

        if (request.Headers.Authorization is null)
        {
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
