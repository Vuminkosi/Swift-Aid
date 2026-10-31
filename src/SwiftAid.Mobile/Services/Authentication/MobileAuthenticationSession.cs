using SwiftAid.Client.Authentication;
using SwiftAid.Mobile.Services.Account;
using SwiftAid.Mobile.Services.Http;

namespace SwiftAid.Mobile.Services.Authentication;

/// <summary>Small UI-facing session facade; credentials never leave the authentication services.</summary>
public sealed class MobileAuthenticationSession : IMobileAuthenticationSession
{
    private readonly NativeLoginClient _loginClient;
    private readonly MobileLogoutService _logoutService;
    private readonly CookieSessionHttpClient _cookieSession;

    public event EventHandler? AuthenticationStateChanged;

    public MobileAuthenticationSession(
        NativeLoginClient loginClient,
        MobileLogoutService logoutService,
        CookieSessionHttpClient cookieSession)
    {
        _loginClient = loginClient ?? throw new ArgumentNullException(nameof(loginClient));
        _logoutService = logoutService ?? throw new ArgumentNullException(nameof(logoutService));
        _cookieSession = cookieSession ?? throw new ArgumentNullException(nameof(cookieSession));
    }

    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await _loginClient.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            return true;
        }

        // Password sign-in keeps its session in the gateway cookie jar rather
        // than the native token store; honor it so the shell reflects it.
        await _cookieSession.EnsureAuthenticationCookieRestoredAsync(cancellationToken).ConfigureAwait(false);
        return _cookieSession.HasAuthenticationCookie;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await IsSignedInAsync(cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    public Task<bool> HasPendingSignOutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_loginClient.HasPendingSignOut);
    }

    public async Task<bool> SignInAsync(CancellationToken cancellationToken = default)
    {
        var result = await _loginClient.LoginAsync(cancellationToken).ConfigureAwait(false);
        var signedIn = result.Status == NativeLoginStatus.Succeeded;
        if (signedIn)
        {
            AuthenticationStateChanged?.Invoke(this, EventArgs.Empty);
        }

        return signedIn;
    }

    public async Task<MobileAuthenticationSignOutResult> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var passwordCookieSignedOut = true;
        try
        {
            await _cookieSession.EnsureAuthenticationCookieRestoredAsync(cancellationToken).ConfigureAwait(false);
            if (_cookieSession.HasAuthenticationCookie)
            {
                var cookieResult = await new HttpPasswordAuthClient(_cookieSession)
                    .LogoutAsync(cancellationToken)
                    .ConfigureAwait(false);
                passwordCookieSignedOut = cookieResult.Status == PasswordAuthStatus.Succeeded;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            passwordCookieSignedOut = false;
        }

        var result = await _logoutService.LogoutAsync(cancellationToken).ConfigureAwait(false);
        // LogoutAsync locks the local session before attempting remote revocation.
        // Notify even when confirmation fails so consumers re-read usable persisted state.
        AuthenticationStateChanged?.Invoke(this, EventArgs.Empty);

        return new MobileAuthenticationSignOutResult(
            result.SignedOut && passwordCookieSignedOut,
            result.DeviceRevocationConfirmed);
    }

    private void NotifyChanged() => AuthenticationStateChanged?.Invoke(this, EventArgs.Empty);
}
