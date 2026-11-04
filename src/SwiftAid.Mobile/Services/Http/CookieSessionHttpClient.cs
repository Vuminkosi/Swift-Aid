using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Authentication;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Mobile.Services.Http;

/// <summary>
/// Cookie-session HTTP transport shared by cookie-authenticated mobile calls.
/// One jar per gateway address; sign-out clears it via <see cref="ClearCookiesAsync"/>.
/// </summary>
public class CookieSessionHttpClient : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CookieSessionHttpClient> SharedByAddress = new(
        StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Production and local Development Gateway auth cookie names. Persist the
    /// Development cookie only for loopback/Android-emulator HTTP origins.
    /// </summary>
    public const string GatewayAuthCookieName = "__Host-SwiftAid.Auth";
    public const string DevelopmentGatewayAuthCookieName = "SwiftAid.Auth.Dev";

    private readonly object _sync = new();
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private HttpClientHandler _handler;
    private HttpClient _client;
    private IGatewayAuthCookiePersistence? _cookiePersistence;
    private TimeProvider _timeProvider = TimeProvider.System;
    private bool _restoreCompleted;
    private bool _disposed;

    /// <summary>True while the jar holds a live gateway auth cookie.</summary>
    public bool HasAuthenticationCookie
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var baseAddress = _client.BaseAddress;
                if (baseAddress is null)
                {
                    return false;
                }

                var cookies = _handler.CookieContainer.GetCookies(baseAddress);
                foreach (System.Net.Cookie cookie in cookies)
                {
                    if (!cookie.Expired && IsGatewayAuthCookieName(cookie.Name))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    private static bool IsGatewayAuthCookieName(string? name) =>
        string.Equals(name, GatewayAuthCookieName, StringComparison.Ordinal) ||
        string.Equals(name, DevelopmentGatewayAuthCookieName, StringComparison.Ordinal);

    public CookieSessionHttpClient(
        string? baseAddress,
        IGatewayAuthCookiePersistence? cookiePersistence = null,
        TimeProvider? timeProvider = null)
    {
        _cookiePersistence = cookiePersistence;
        _timeProvider = timeProvider ?? TimeProvider.System;
        (_handler, _client) = CreateClient(baseAddress);
    }

    private static (HttpClientHandler Handler, HttpClient Client) CreateClient(string? baseAddress)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (Uri.TryCreate(baseAddress, UriKind.Absolute, out var baseUri))
        {
            client.BaseAddress = baseUri;
        }

        return (handler, client);
    }

    public static CookieSessionHttpClient Shared(
        string? baseAddress,
        IGatewayAuthCookiePersistence? cookiePersistence = null,
        TimeProvider? timeProvider = null)
    {
        var session = SharedByAddress.GetOrAdd(
            Normalize(baseAddress),
            key => new CookieSessionHttpClient(key, cookiePersistence, timeProvider));
        if (cookiePersistence is not null)
        {
            session.AttachCookiePersistence(cookiePersistence, timeProvider);
        }

        return session;
    }

    private void AttachCookiePersistence(
        IGatewayAuthCookiePersistence cookiePersistence,
        TimeProvider? timeProvider)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cookiePersistence is null)
            {
                _cookiePersistence = cookiePersistence;
                _timeProvider = timeProvider ?? _timeProvider;
                _restoreCompleted = false;
            }
        }
    }

    /// <summary>Restores a remembered Gateway session from protected device storage.</summary>
    public async Task EnsureAuthenticationCookieRestoredAsync(CancellationToken cancellationToken = default)
    {
        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IGatewayAuthCookiePersistence? persistence;
            Uri? gateway;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_restoreCompleted)
                {
                    return;
                }

                persistence = _cookiePersistence;
                gateway = _client.BaseAddress;
            }

            if (persistence is null)
            {
                lock (_sync) { _restoreCompleted = true; }
                return;
            }

            var snapshot = await persistence.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                System.Net.Cookie? cookie = null;
                if (gateway is not null)
                {
                    try
                    {
                        cookie = snapshot.RestoreFor(gateway, _timeProvider.GetUtcNow());
                    }
                    catch (System.Net.CookieException)
                    {
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                if (cookie is null)
                {
                    await persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        lock (_sync)
                        {
                            ObjectDisposedException.ThrowIf(_disposed, this);
                            _handler.CookieContainer.Add(gateway!, cookie);
                        }
                    }
                    catch (System.Net.CookieException)
                    {
                        await persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (ArgumentException)
                    {
                        await persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            lock (_sync) { _restoreCompleted = true; }
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    /// <summary>Saves a persistent auth cookie; session-only cookies are never written to storage.</summary>
    public async Task<bool> PersistAuthenticationCookieAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticationCookieRestoredAsync(cancellationToken).ConfigureAwait(false);
        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IGatewayAuthCookiePersistence? persistence;
            Uri? gateway;
            System.Net.Cookie? cookie;
            TimeProvider timeProvider;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                persistence = _cookiePersistence;
                gateway = _client.BaseAddress;
                timeProvider = _timeProvider;
                cookie = gateway is null
                    ? null
                    : FindPersistableAuthenticationCookie(
                        gateway,
                        _handler.CookieContainer.GetCookies(gateway),
                        timeProvider.GetUtcNow());
            }

            var snapshot = gateway is null
                ? null
                : GatewayAuthCookieSnapshot.Capture(gateway, cookie, timeProvider.GetUtcNow());
            if (persistence is null || snapshot is null)
            {
                if (persistence is not null)
                {
                    await persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
                }

                return false;
            }

            await persistence.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    public async Task ClearCookiesAsync(CancellationToken cancellationToken = default)
    {
        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IGatewayAuthCookiePersistence? persistence;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                persistence = _cookiePersistence;
                var baseAddress = _client.BaseAddress?.ToString();
                _client.Dispose();
                _handler.Dispose();
                (_handler, _client) = CreateClient(baseAddress);
                _restoreCompleted = true;
            }

            if (persistence is not null)
            {
                await persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    public virtual async Task<string?> GetAntiforgeryTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticationCookieRestoredAsync(cancellationToken).ConfigureAwait(false);
        HttpClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
        }

        if (client.BaseAddress is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/antiforgery");
        using var response = await SendAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("requestToken", out var token)
            ? token.GetString()
            : null;
    }

    /// <summary>Gateway base address used for absolute same-origin links.</summary>
    public string? BaseAddress
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _client.BaseAddress?.ToString();
            }
        }
    }

    /// <summary>Returns cookies currently held for an absolute same-origin
    /// Gateway request. Used by the Ellai transport to share the existing
    /// password-session and guest-session cookie jar.</summary>
    public string GetCookieHeader(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsWithinBaseAddress(_client.BaseAddress, destination))
            {
                throw new InvalidOperationException("Cookies may only be read for this configured Gateway.");
            }

            return _handler.CookieContainer.GetCookieHeader(destination);
        }
    }

    /// <summary>Stores Gateway Set-Cookie values in the existing account and
    /// guest cookie container; cookies from other origins are ignored.</summary>
    public void StoreCookies(Uri destination, IEnumerable<string> setCookieHeaders)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(setCookieHeaders);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsWithinBaseAddress(_client.BaseAddress, destination))
            {
                return;
            }

            foreach (var setCookieHeader in setCookieHeaders)
            {
                if (string.IsNullOrWhiteSpace(setCookieHeader))
                {
                    continue;
                }

                try
                {
                    _handler.CookieContainer.SetCookies(destination, setCookieHeader);
                }
                catch (System.Net.CookieException)
                {
                    // Ignore malformed remote cookie metadata without logging its value.
                }
            }
        }
    }

    private static bool IsWithinBaseAddress(Uri? baseAddress, Uri destination)
    {
        if (baseAddress is null || !destination.IsAbsoluteUri ||
            !string.Equals(baseAddress.Scheme, destination.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseAddress.IdnHost, destination.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            baseAddress.Port != destination.Port)
        {
            return false;
        }

        var prefix = baseAddress.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? baseAddress.AbsolutePath
            : baseAddress.AbsolutePath + "/";
        return destination.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ||
            string.Equals(destination.AbsolutePath.TrimEnd('/'), baseAddress.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    public virtual async Task<HttpResponseMessage> DeleteAsync(
        string path,
        string? antiforgeryToken,
        CancellationToken cancellationToken = default)
    {
        HttpClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        if (!string.IsNullOrWhiteSpace(antiforgeryToken))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", antiforgeryToken);
        }

        return await SendAsync(client, request, cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<HttpResponseMessage> PutFormAsync(
        string path,
        MultipartFormDataContent form,
        string? antiforgeryToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        HttpClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = form
        };
        if (!string.IsNullOrWhiteSpace(antiforgeryToken))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", antiforgeryToken);
        }

        return await SendAsync(client, request, cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        HttpClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync(client, request, cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<HttpResponseMessage> PostJsonAsync<T>(
        string path,
        T body,
        string? antiforgeryToken,
        CancellationToken cancellationToken = default) =>
        await SendJsonAsync(HttpMethod.Post, path, body, antiforgeryToken, cancellationToken).ConfigureAwait(false);

    public virtual async Task<HttpResponseMessage> PutJsonAsync<T>(
        string path,
        T body,
        string? antiforgeryToken,
        CancellationToken cancellationToken = default) =>
        await SendJsonAsync(HttpMethod.Put, path, body, antiforgeryToken, cancellationToken).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        T body,
        string? antiforgeryToken,
        CancellationToken cancellationToken)
    {
        HttpClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
        }

        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, MediaTypeHeaderValue.Parse("application/json"), JsonOptions)
        };
        if (!string.IsNullOrWhiteSpace(antiforgeryToken))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", antiforgeryToken);
        }

        return await SendAsync(client, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await EnsureAuthenticationCookieRestoredAsync(cancellationToken).ConfigureAwait(false);
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await RefreshPersistedCookieAsync().ConfigureAwait(false);
        return response;
    }

    private static System.Net.Cookie? FindPersistableAuthenticationCookie(
        Uri gateway,
        System.Net.CookieCollection cookies,
        DateTimeOffset now)
    {
        foreach (System.Net.Cookie cookie in cookies)
        {
            if (GatewayAuthCookieSnapshot.Capture(gateway, cookie, now) is not null)
            {
                return cookie;
            }
        }

        return null;
    }

    private async Task RefreshPersistedCookieAsync()
    {
        await _restoreGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            IGatewayAuthCookiePersistence? persistence;
            Uri? gateway;
            System.Net.Cookie? cookie;
            TimeProvider timeProvider;
            lock (_sync)
            {
                persistence = _cookiePersistence;
                gateway = _client.BaseAddress;
                timeProvider = _timeProvider;
                cookie = _disposed || gateway is null
                    ? null
                    : FindPersistableAuthenticationCookie(
                        gateway,
                        _handler.CookieContainer.GetCookies(gateway),
                        timeProvider.GetUtcNow());
            }

            if (persistence is null || gateway is null)
            {
                return;
            }

            var snapshot = GatewayAuthCookieSnapshot.Capture(gateway, cookie, timeProvider.GetUtcNow());
            if (snapshot is null)
            {
                return;
            }

            try
            {
                await persistence.WriteAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Do not turn a completed Gateway operation into an apparent failure
                // because the device keychain is temporarily unavailable.
            }
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
        _handler.Dispose();
    }

    private static string Normalize(string? baseAddress) =>
        string.IsNullOrWhiteSpace(baseAddress) ? string.Empty : baseAddress.Trim().TrimEnd('/');
}
