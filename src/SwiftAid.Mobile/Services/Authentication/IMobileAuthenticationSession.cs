namespace SwiftAid.Mobile.Services.Authentication;

public sealed record MobileAuthenticationSignOutResult(bool SignedOut, bool DeviceNotificationsStopped);

public interface IMobileAuthenticationSession
{
    event EventHandler? AuthenticationStateChanged;

    Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default);

    Task<bool> HasPendingSignOutAsync(CancellationToken cancellationToken = default);

    Task<bool> SignInAsync(CancellationToken cancellationToken = default);

    Task<MobileAuthenticationSignOutResult> SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads the current session (native tokens or gateway cookie) and
    /// notifies subscribers so the shell refreshes without an app restart.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
