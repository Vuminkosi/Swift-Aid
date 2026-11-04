using SwiftAid.Client.Authentication;
using SwiftAid.Client.Notifications;

namespace SwiftAid.Mobile.Services.Authentication;

public sealed record MobileLogoutResult(NotificationPlatformResult DeviceRevocationStatus, bool SignedOut)
{
    public bool DeviceRevocationConfirmed => DeviceRevocationStatus == NotificationPlatformResult.Disabled;
}

/// <summary>Attempts to revoke this installation before the native session is cleared.</summary>
public sealed class MobileLogoutService
{
    private readonly INotificationPlatformSettings _notificationSettings;
    private readonly NativeLoginClient _nativeLoginClient;

    public MobileLogoutService(
        INotificationPlatformSettings notificationSettings,
        NativeLoginClient nativeLoginClient)
    {
        _notificationSettings = notificationSettings ?? throw new ArgumentNullException(nameof(notificationSettings));
        _nativeLoginClient = nativeLoginClient ?? throw new ArgumentNullException(nameof(nativeLoginClient));
    }

    public async Task<MobileLogoutResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        // Immediately stop issuing credentials while the server-side installation revoke runs.
        // The secure token remains available only for the authenticated revoke request.
        _nativeLoginClient.LockSession();

        NotificationPlatformResult revocationStatus;
        try
        {
            // A user-initiated logout still attempts revocation if the UI token is cancelled.
            revocationStatus = await _notificationSettings
                .DisableAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An uncertain revoke must keep the local session locked and intact for a safe retry.
            revocationStatus = NotificationPlatformResult.Unavailable;
        }

        if (revocationStatus != NotificationPlatformResult.Disabled)
        {
            return new MobileLogoutResult(revocationStatus, SignedOut: false);
        }

        try
        {
            await _nativeLoginClient.LogoutAsync(cancellationToken).ConfigureAwait(false);
            return new MobileLogoutResult(revocationStatus, SignedOut: true);
        }
        catch (Exception)
        {
            // Keep the session locked; a later retry can confirm remote sign-out before clearing it.
            return new MobileLogoutResult(revocationStatus, SignedOut: false);
        }
    }
}
