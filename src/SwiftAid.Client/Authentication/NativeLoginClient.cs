using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;

namespace SwiftAid.Client.Authentication;

/// <summary>Public OIDC settings supplied by the application host.</summary>
public sealed record NativeLoginOptions(
    string Authority,
    string ClientId,
    string RedirectUri,
    string PostLogoutRedirectUri)
{
    public void Validate()
    {
        ValidateAbsoluteUri(Authority, nameof(Authority), requireHttps: true);
        ValidateAbsoluteUri(RedirectUri, nameof(RedirectUri), requireHttps: false);
        ValidateAbsoluteUri(PostLogoutRedirectUri, nameof(PostLogoutRedirectUri), requireHttps: false);

        if (string.IsNullOrWhiteSpace(ClientId) || ClientId != ClientId.Trim() || ClientId.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A canonical native client identifier is required.", nameof(ClientId));
        }
    }

    private static void ValidateAbsoluteUri(string value, string parameterName, bool requireHttps)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            (requireHttps && uri.Scheme != Uri.UriSchemeHttps) ||
            (!requireHttps && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback) ||
            (!requireHttps && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp &&
                (!string.IsNullOrEmpty(uri.Host) || !uri.AbsolutePath.StartsWith("/", StringComparison.Ordinal))))
        {
            throw new ArgumentException("A valid absolute URI is required.", parameterName);
        }
    }
}

/// <summary>The small SwiftAid session persisted by the platform secure-storage adapter.</summary>
public sealed record NativeLoginSession(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    string? RefreshToken = null,
    string? IdentityToken = null);

public enum NativeLoginStatus
{
    Succeeded,
    Cancelled,
    Failed
}

/// <summary>Safe result that never includes raw provider or protocol error details.</summary>
public sealed record NativeLoginOutcome(NativeLoginStatus Status, string? ErrorCode = null);

public interface INativeSessionStore : INativeSessionReader
{
    Task StoreAsync(NativeLoginSession session, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Factory seam for protocol tests. Production uses Duende IdentityModel OIDC Client.</summary>
public interface INativeOidcClientFactory
{
    INativeOidcClient Create(NativeLoginOptions options, IBrowser browser);
}

public interface INativeOidcClient
{
    Task<NativeOidcLoginResult> LoginAsync(CancellationToken cancellationToken);

    Task<NativeOidcLoginResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task<bool> LogoutAsync(string? idTokenHint, CancellationToken cancellationToken);
}

public sealed record NativeOidcLoginResult(
    bool IsError,
    string? Error,
    string? AccessToken,
    DateTimeOffset AccessTokenExpiration,
    string? IdentityToken,
    string? RefreshToken = null);

/// <summary>Runs system-browser authorization through Duende's RFC 8252 OIDC client.</summary>
public sealed class NativeLoginClient
{
    private readonly NativeLoginOptions _options;
    private readonly INativeOidcClient _client;
    private readonly INativeSessionStore _sessionStore;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _generationLock = new();
    private long _loginGeneration;
    private CancellationTokenSource? _activeLogin;
    private string? _identityTokenForLogout;
    private bool _sessionLocked;

    public NativeLoginClient(
        NativeLoginOptions options,
        INativeOidcClientFactory clientFactory,
        IBrowser browser,
        INativeSessionStore sessionStore)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(browser);
        _client = clientFactory.Create(_options, browser);
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
    }

    public bool HasPendingSignOut
    {
        get
        {
            lock (_generationLock)
            {
                return _sessionLocked;
            }
        }
    }

    public async Task<NativeLoginOutcome> LoginAsync(CancellationToken cancellationToken)
    {
        long generation;
        lock (_generationLock)
        {
            if (_sessionLocked)
            {
                return new NativeLoginOutcome(NativeLoginStatus.Failed, "sign_out_incomplete");
            }

            generation = _loginGeneration;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? loginCancellation = null;
        try
        {
            loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_generationLock)
            {
                if (generation != _loginGeneration)
                {
                    return new NativeLoginOutcome(NativeLoginStatus.Cancelled);
                }

                Interlocked.Exchange(ref _activeLogin, loginCancellation);
            }

            var result = await _client.LoginAsync(loginCancellation.Token).ConfigureAwait(false);
            if (!IsCurrentGeneration(generation))
            {
                return new NativeLoginOutcome(NativeLoginStatus.Cancelled);
            }

            if (result.IsError)
            {
                return IsCancellation(result.Error)
                    ? new NativeLoginOutcome(NativeLoginStatus.Cancelled)
                    : new NativeLoginOutcome(NativeLoginStatus.Failed, "sign_in_failed");
            }

            if (string.IsNullOrWhiteSpace(result.AccessToken) ||
                string.IsNullOrWhiteSpace(result.RefreshToken) ||
                result.AccessTokenExpiration <= DateTimeOffset.UtcNow)
            {
                return new NativeLoginOutcome(NativeLoginStatus.Failed, "invalid_sign_in_response");
            }

            var session = new NativeLoginSession(
                result.AccessToken,
                result.AccessTokenExpiration,
                result.RefreshToken,
                result.IdentityToken);
            try
            {
                await _sessionStore.StoreAsync(session, loginCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The caller's own token is the only honest cancellation signal here:
                // LockSession below always bumps the generation and cancels the linked
                // attempt, so reading those afterwards would misreport a storage
                // failure as a user cancellation.
                var callerCancelled = cancellationToken.IsCancellationRequested;
                LockSession();
                try
                {
                    await _sessionStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A storage implementation owns its platform diagnostics; expose only a safe result.
                }

                return callerCancelled
                    ? new NativeLoginOutcome(NativeLoginStatus.Cancelled)
                    : new NativeLoginOutcome(NativeLoginStatus.Failed, "session_storage_failed");
            }

            bool superseded;
            lock (_generationLock)
            {
                superseded = generation != _loginGeneration || _sessionLocked;
                if (!superseded)
                {
                    _sessionLocked = false;
                    _identityTokenForLogout = string.IsNullOrWhiteSpace(result.IdentityToken)
                        ? null
                        : result.IdentityToken;
                }
            }

            if (superseded)
            {
                await _sessionStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                return new NativeLoginOutcome(NativeLoginStatus.Cancelled);
            }

            return new NativeLoginOutcome(NativeLoginStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            return new NativeLoginOutcome(NativeLoginStatus.Cancelled);
        }
        catch (Exception)
        {
            return new NativeLoginOutcome(NativeLoginStatus.Failed, "sign_in_failed");
        }
        finally
        {
            if (loginCancellation is not null)
            {
                Interlocked.CompareExchange(ref _activeLogin, null, loginCancellation);
                loginCancellation.Dispose();
            }

            _operationGate.Release();
        }
    }

    /// <summary>Returns a current session, rotating its refresh token before protected requests.</summary>
    public async Task<NativeLoginSession?> GetCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        lock (_generationLock)
        {
            if (_sessionLocked)
            {
                return null;
            }
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_generationLock)
            {
                if (_sessionLocked)
                {
                    return null;
                }

                generation = _loginGeneration;
            }

            var session = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                return null;
            }

            if (session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                lock (_generationLock)
                {
                    return !_sessionLocked && generation == _loginGeneration ? session : null;
                }
            }

            if (string.IsNullOrWhiteSpace(session.RefreshToken))
            {
                LockSession();
                return null;
            }

            NativeOidcLoginResult refreshed;
            try
            {
                refreshed = await _client.RefreshAsync(session.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }

            if (refreshed.IsError || string.IsNullOrWhiteSpace(refreshed.AccessToken) ||
                refreshed.AccessTokenExpiration <= DateTimeOffset.UtcNow ||
                string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            {
                LockSession();
                // Retain secure credentials until device revocation is confirmed; the locked
                // bearer handler will not use them for ordinary requests.
                return null;
            }

            var rotated = new NativeLoginSession(
                refreshed.AccessToken,
                refreshed.AccessTokenExpiration,
                refreshed.RefreshToken,
                string.IsNullOrWhiteSpace(refreshed.IdentityToken) ? session.IdentityToken : refreshed.IdentityToken);
            await _sessionStore.StoreAsync(rotated, cancellationToken).ConfigureAwait(false);
            bool superseded;
            lock (_generationLock)
            {
                superseded = _sessionLocked || generation != _loginGeneration;
                if (!superseded)
                {
                    _identityTokenForLogout = rotated.IdentityToken;
                }
            }

            if (superseded)
            {
                await _sessionStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            return rotated;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Supplies a bearer only to the explicitly marked installation-revocation request after
    /// normal session use has been locked. It never reopens the session for other API calls.
    /// </summary>
    internal async Task<NativeLoginSession?> GetDeviceRevocationSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_generationLock)
            {
                if (!_sessionLocked)
                {
                    return null;
                }
            }

            var session = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                return null;
            }

            if (session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return session;
            }

            if (string.IsNullOrWhiteSpace(session.RefreshToken))
            {
                return null;
            }

            var refreshed = await _client.RefreshAsync(session.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (refreshed.IsError || string.IsNullOrWhiteSpace(refreshed.AccessToken) ||
                refreshed.AccessTokenExpiration <= DateTimeOffset.UtcNow ||
                string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            {
                return null;
            }

            var rotated = new NativeLoginSession(
                refreshed.AccessToken,
                refreshed.AccessTokenExpiration,
                refreshed.RefreshToken,
                string.IsNullOrWhiteSpace(refreshed.IdentityToken) ? session.IdentityToken : refreshed.IdentityToken);
            await _sessionStore.StoreAsync(rotated, cancellationToken).ConfigureAwait(false);
            lock (_generationLock)
            {
                if (!_sessionLocked)
                {
                    return null;
                }

                _identityTokenForLogout = rotated.IdentityToken;
            }

            return rotated;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Locks authentication immediately while installation revocation is in progress.</summary>
    public void LockSession()
    {
        lock (_generationLock)
        {
            _loginGeneration = unchecked(_loginGeneration + 1);
            _sessionLocked = true;
        }

        try
        {
            Volatile.Read(ref _activeLogin)?.Cancel();
        }
        catch (Exception)
        {
            // A cancellation callback must not prevent local session clearing.
        }

    }

    /// <summary>Clears local credentials only after remote installation revocation is confirmed.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        LockSession();
        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            string? idTokenHint;
            NativeLoginSession? persistedSession = null;
            try
            {
                persistedSession = await _sessionStore.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                throw new InvalidOperationException("The saved session could not be confirmed.");
            }

            lock (_generationLock)
            {
                idTokenHint = _identityTokenForLogout ?? persistedSession?.IdentityToken;
                _identityTokenForLogout = null;
            }

            if (persistedSession is not null)
            {
                bool remoteLogoutConfirmed;
                try
                {
                    remoteLogoutConfirmed = await _client.LogoutAsync(idTokenHint, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    throw new InvalidOperationException("Remote sign-out could not be confirmed.");
                }

                if (!remoteLogoutConfirmed)
                {
                    throw new InvalidOperationException("Remote sign-out could not be confirmed.");
                }
            }

            await _sessionStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_generationLock)
            {
                _identityTokenForLogout = null;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static bool IsCancellation(string? error) =>
        string.Equals(error, "UserCancel", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(error, "user_cancelled", StringComparison.OrdinalIgnoreCase);

    private bool IsCurrentGeneration(long generation)
    {
        lock (_generationLock)
        {
            return generation == _loginGeneration;
        }
    }
}

/// <summary>Adapter that delegates all OIDC protocol work to the maintained Duende client.</summary>
public sealed class DuendeNativeOidcClientFactory : INativeOidcClientFactory
{
    public INativeOidcClient Create(NativeLoginOptions options, IBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(browser);
        options.Validate();

        var client = new OidcClient(new OidcClientOptions
        {
            Authority = options.Authority,
            ClientId = options.ClientId,
            RedirectUri = options.RedirectUri,
            PostLogoutRedirectUri = options.PostLogoutRedirectUri,
            Scope = "openid offline_access",
            Browser = browser,
            LoadProfile = false
        });

        return new DuendeNativeOidcClient(client);
    }

    private sealed class DuendeNativeOidcClient(OidcClient client) : INativeOidcClient
    {
        public async Task<NativeOidcLoginResult> LoginAsync(CancellationToken cancellationToken)
        {
            var result = await client.LoginAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new NativeOidcLoginResult(
                result.IsError,
                result.Error,
                result.AccessToken,
                result.AccessTokenExpiration,
                result.IdentityToken,
                result.RefreshToken);
        }

        public async Task<NativeOidcLoginResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            var result = await client.RefreshTokenAsync(refreshToken, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new NativeOidcLoginResult(
                result.IsError,
                result.Error,
                result.AccessToken,
                result.AccessTokenExpiration,
                result.IdentityToken,
                result.RefreshToken);
        }

        public async Task<bool> LogoutAsync(string? idTokenHint, CancellationToken cancellationToken)
        {
            var result = await client.LogoutAsync(
                new LogoutRequest { IdTokenHint = idTokenHint }, cancellationToken).ConfigureAwait(false);
            return !result.IsError;
        }
    }
}
