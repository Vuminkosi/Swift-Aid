namespace SwiftAid.Mobile.Services.Authentication;

/// <summary>
/// Fail-closed session used when no native identity backend is configured.
/// Never reports a signed-in user and never fabricates a sign-in result.
/// </summary>
public sealed class UnavailableMobileAuthenticationSession : IMobileAuthenticationSession
{
    public event EventHandler? AuthenticationStateChanged
    {
        add { }
        remove { }
    }

    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> HasPendingSignOutAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> SignInAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<MobileAuthenticationSignOutResult> SignOutAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MobileAuthenticationSignOutResult(SignedOut: true, DeviceNotificationsStopped: true));
}
