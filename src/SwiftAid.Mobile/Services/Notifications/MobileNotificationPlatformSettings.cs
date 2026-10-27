using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Plugin.Firebase.CloudMessaging;
using SwiftAid.Client.Notifications;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Mobile.Services.Notifications;

/// <summary>Opt-in push registration for the current authenticated installation.</summary>
public sealed class MobileNotificationPlatformSettings : INotificationPlatformSettings
{
    private const string EnabledKey = "notifications.push.enabled";
    private const string SoundKey = "notifications.sound.enabled";
    private const string InstallationKey = "notifications.installation.id";
    private const string PlayedKey = "notifications.sound.played";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object SoundGate = new();
    private readonly INotificationFeedClient _feed;
#if ANDROID
    private static Android.Media.MediaPlayer? _androidPlayer;
#elif IOS
    private static AVFoundation.AVAudioPlayer? _iosPlayer;
#endif

    public MobileNotificationPlatformSettings(INotificationFeedClient feed)
    {
        _feed = feed;
#if ANDROID || IOS
        CrossFirebaseCloudMessaging.Current.TokenChanged += HandleTokenChanged;
#endif
    }

    public async Task<NotificationPlatformSettingsSnapshot> GetStateAsync(CancellationToken cancellationToken = default) =>
        new(await GetPermissionStatusAsync(cancellationToken).ConfigureAwait(false),
            Preferences.Get(EnabledKey, false), Preferences.Get(SoundKey, false));

    public Task<NotificationPermissionState> GetPermissionStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if ANDROID || IOS
        return GetSupportedPermissionStatusAsync();
#else
        return Task.FromResult(NotificationPermissionState.NotSupported);
#endif
    }

    public async Task<NotificationPlatformResult> EnableAsync(bool playGentleSound, CancellationToken cancellationToken = default)
    {
#if ANDROID || IOS
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await RequestPermissionAsync().ConfigureAwait(false))
                return NotificationPlatformResult.PermissionDenied;

            var installationId = GetInstallationId();
            await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync().ConfigureAwait(false);
            var token = await CrossFirebaseCloudMessaging.Current.GetTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return NotificationPlatformResult.Unavailable;

#if ANDROID
            MobileNotificationAndroidResources.EnsureGentleChannel();
#endif
            var platform = DeviceInfo.Platform == DevicePlatform.iOS
                ? PushDevicePlatform.Ios
                : PushDevicePlatform.Android;
            var result = await _feed.RegisterDeviceAsync(installationId, platform, token, playGentleSound, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == PushDeviceRegistrationStatus.Registered)
            {
                Preferences.Set(SoundKey, playGentleSound);
                Preferences.Set(EnabledKey, true);
                return NotificationPlatformResult.Enabled;
            }

            return result.Status == PushDeviceRegistrationStatus.NotAuthenticated
                ? NotificationPlatformResult.NotAuthenticated
                : NotificationPlatformResult.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NotificationPlatformResult.Unavailable;
        }
        finally
        {
            Gate.Release();
        }
#else
        return NotificationPlatformResult.NotSupported;
#endif
    }

    public async Task<NotificationPlatformResult> SetGentleSoundAsync(bool enabled, CancellationToken cancellationToken = default)
    {
#if ANDROID || IOS
        if (!Preferences.Get(EnabledKey, false))
            return NotificationPlatformResult.Disabled;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = await CrossFirebaseCloudMessaging.Current.GetTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return NotificationPlatformResult.Unavailable;
            var platform = DeviceInfo.Platform == DevicePlatform.iOS
                ? PushDevicePlatform.Ios
                : PushDevicePlatform.Android;
            var result = await _feed.RegisterDeviceAsync(
                GetInstallationId(), platform, token, enabled, cancellationToken).ConfigureAwait(false);
            if (result.Status == PushDeviceRegistrationStatus.Registered)
            {
                Preferences.Set(SoundKey, enabled);
                return NotificationPlatformResult.Enabled;
            }
            return result.Status == PushDeviceRegistrationStatus.NotAuthenticated
                ? NotificationPlatformResult.NotAuthenticated
                : NotificationPlatformResult.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NotificationPlatformResult.Unavailable;
        }
        finally
        {
            Gate.Release();
        }
#else
        return NotificationPlatformResult.NotSupported;
#endif
    }

    public async Task<NotificationPlatformResult> DisableAsync(CancellationToken cancellationToken = default)
    {
        PushDeviceRemovalResult result;
        try
        {
            result = await _feed.RemoveDeviceAsync(GetInstallationId(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NotificationPlatformResult.Unavailable;
        }
        switch (result.Status)
        {
            case PushDeviceRemovalStatus.Removed:
            case PushDeviceRemovalStatus.NotFoundOrNotAuthorized:
                Preferences.Set(SoundKey, false);
                Preferences.Set(EnabledKey, false);
                return NotificationPlatformResult.Disabled;
            case PushDeviceRemovalStatus.NotAuthenticated:
                return NotificationPlatformResult.NotAuthenticated;
            default:
                return NotificationPlatformResult.Unavailable;
        }
    }

    public Task<bool> TryPlayForegroundSoundAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (occurrenceId == Guid.Empty ||
            !Preferences.Get(EnabledKey, false) ||
            !Preferences.Get(SoundKey, false))
        {
            return Task.FromResult(false);
        }

#if ANDROID
        if (!MobileNotificationForeground.IsForeground)
            return Task.FromResult(false);
#elif IOS
        if (UIKit.UIApplication.SharedApplication.ApplicationState != UIKit.UIApplicationState.Active)
            return Task.FromResult(false);
#else
        return Task.FromResult(false);
#endif

        lock (SoundGate)
        {
            if (HasPlayed(occurrenceId))
                return Task.FromResult(false);
            try
            {
#if ANDROID
                var assets = Android.App.Application.Context.Assets ??
                    throw new InvalidOperationException("The Android asset manager is unavailable.");
                using var descriptor = assets.OpenFd("swiftaid_gentle_notification.wav");
                _androidPlayer?.Stop();
                _androidPlayer?.Release();
                var player = new Android.Media.MediaPlayer();
                player.SetDataSource(descriptor.FileDescriptor, descriptor.StartOffset, descriptor.Length);
                player.Completion += (_, _) =>
                {
                    player.Stop();
                    player.Release();
                    _androidPlayer = null;
                };
                player.Prepare();
                player.Start();
                _androidPlayer = player;
#elif IOS
                var path = Foundation.NSBundle.MainBundle.PathForResource("swiftaid_gentle_notification", "wav");
                if (path is null)
                    return Task.FromResult(false);
                _iosPlayer?.Stop();
                _iosPlayer = AVFoundation.AVAudioPlayer.FromUrl(Foundation.NSUrl.FromFilename(path));
                if (_iosPlayer?.Play() != true)
                    return Task.FromResult(false);
#endif
                MarkPlayed(occurrenceId);
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }
    }
    private static bool HasPlayed(Guid occurrenceId) => GetPlayedIds().Contains(occurrenceId);

    private static void MarkPlayed(Guid occurrenceId)
    {
        var ids = GetPlayedIds();
        ids.Add(occurrenceId);
        Preferences.Set(PlayedKey, string.Join(';', ids.TakeLast(100)));
    }

    private static List<Guid> GetPlayedIds() => Preferences.Get(PlayedKey, string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .ToList();

    private async void HandleTokenChanged(object? sender, Plugin.Firebase.CloudMessaging.EventArgs.FCMTokenChangedEventArgs args)
    {
        if (!Preferences.Get(EnabledKey, false) || string.IsNullOrWhiteSpace(args.Token)) return;
        try
        {
            var platform = DeviceInfo.Platform == DevicePlatform.iOS ? PushDevicePlatform.Ios : PushDevicePlatform.Android;
            await _feed.RegisterDeviceAsync(GetInstallationId(), platform, args.Token, Preferences.Get(SoundKey, false)).ConfigureAwait(false);
        }
        catch
        {
            // A later token refresh or explicit re-enable retries registration; never log the token.
        }
    }

    private static Guid GetInstallationId()
    {
        var value = Preferences.Get(InstallationKey, string.Empty);
        if (Guid.TryParse(value, out var id) && id != Guid.Empty)
            return id;
        id = Guid.NewGuid();
        Preferences.Set(InstallationKey, id.ToString("D"));
        return id;
    }

#if ANDROID || IOS
    private static async Task<bool> RequestPermissionAsync()
    {
        var status = await Permissions.RequestAsync<Permissions.PostNotifications>().ConfigureAwait(false);
#if IOS
        if (status == PermissionStatus.Granted)
            UIKit.UIApplication.SharedApplication.RegisterForRemoteNotifications();
#endif
        return status == PermissionStatus.Granted;
    }

    private static async Task<NotificationPermissionState> GetSupportedPermissionStatusAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>().ConfigureAwait(false);
        return status switch
        {
            PermissionStatus.Granted => NotificationPermissionState.Granted,
            PermissionStatus.Denied or PermissionStatus.Restricted => NotificationPermissionState.Denied,
            _ => NotificationPermissionState.Unknown
        };
    }
#endif
}
