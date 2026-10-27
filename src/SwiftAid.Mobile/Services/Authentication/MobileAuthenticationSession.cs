using SwiftAid.Client.Authentication;

namespace SwiftAid.Mobile.Services.Authentication;

/// <summary>Small UI-facing session facade; credentials never leave the authentication services.</summary>
public sealed class MobileAuthenticationSession : IMobileAuthenticationSession
{
    private readonly NativeLoginClient _loginClient;
    private readonly MobileLogoutService _logoutService;

    public MobileAuthenticationSession(
        NativeLoginClient loginClient,
        MobileLogoutService logoutService)
    {
        _loginClient = loginClient ?? throw new ArgumentNullException(nameof(loginClient));
        _logoutService = logoutService ?? throw new ArgumentNullException(nameof(logoutService));
    }

    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default) =>
        await _loginClient.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false) is not null;

    public Task<bool> HasPendingSignOutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_loginClient.HasPendingSignOut);
    }

    public async Task<bool> SignInAsync(CancellationToken cancellationToken = default)
    {
        var result = await _loginClient.LoginAsync(cancellationToken).ConfigureAwait(false);
        return result.Status == NativeLoginStatus.Succeeded;
    }

    public async Task<MobileAuthenticationSignOutResult> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var result = await _logoutService.LogoutAsync(cancellationToken).ConfigureAwait(false);
        return new MobileAuthenticationSignOutResult(result.SignedOut, result.DeviceRevocationConfirmed);
    }
}
