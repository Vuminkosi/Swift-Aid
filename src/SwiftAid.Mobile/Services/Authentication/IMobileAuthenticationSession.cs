namespace SwiftAid.Mobile.Services.Authentication;

public sealed record MobileAuthenticationSignOutResult(bool SignedOut, bool DeviceNotificationsStopped);

public interface IMobileAuthenticationSession
{
    Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default);

    Task<bool> HasPendingSignOutAsync(CancellationToken cancellationToken = default);

    Task<bool> SignInAsync(CancellationToken cancellationToken = default);

    Task<MobileAuthenticationSignOutResult> SignOutAsync(CancellationToken cancellationToken = default);
}
