using System.Net.Http.Headers;

namespace SwiftAid.Client.Authentication;

/// <summary>
/// Adds a current native bearer token only to requests beneath the configured HTTPS gateway
/// base address. The primary transport must disable automatic redirects.
/// </summary>
public sealed class NativeSessionBearerHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<bool> DeviceRevocationRequest =
        new("SwiftAid.DeviceRevocationRequest");

    private readonly NativeLoginClient _loginClient;
    private readonly NativeSessionHttpOptions _options;
    private readonly TimeProvider _timeProvider;

    public NativeSessionBearerHandler(
        INativeSessionReader sessionReader,
        NativeSessionHttpOptions options,
        NativeLoginClient loginClient,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sessionReader);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loginClient = loginClient ?? throw new ArgumentNullException(nameof(loginClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is null || !_options.IsWithinConfiguredGateway(request.RequestUri))
        {
            throw new InvalidOperationException(
                "Authenticated notification requests must target the configured HTTPS gateway.");
        }

        if (request.Headers.Authorization is not null)
        {
            throw new InvalidOperationException(
                "Notification authorization is supplied only by the active native session.");
        }

        var isDeviceRevocation = request.Options.TryGetValue(DeviceRevocationRequest, out var marked) && marked &&
            request.Method == HttpMethod.Delete &&
            request.RequestUri.AbsolutePath.StartsWith("/api/v1/push/devices/", StringComparison.Ordinal) &&
            Guid.TryParse(request.RequestUri.AbsolutePath["/api/v1/push/devices/".Length..], out var installationId) &&
            installationId != Guid.Empty;

        var session = isDeviceRevocation
            ? await _loginClient.GetDeviceRevocationSessionAsync(cancellationToken).ConfigureAwait(false)
            : await _loginClient.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is not null &&
            !string.IsNullOrWhiteSpace(session.AccessToken) &&
            session.ExpiresAtUtc > _timeProvider.GetUtcNow())
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }

        // The configured primary handler uses AllowAutoRedirect=false. A 3xx is returned to
        // the caller without following it, so an Authorization header cannot reach another host.
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
