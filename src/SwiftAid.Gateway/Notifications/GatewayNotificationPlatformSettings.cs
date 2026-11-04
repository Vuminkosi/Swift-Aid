using System.Text.Json;
using Microsoft.JSInterop;
using SwiftAid.Client.Notifications;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Gateway.Notifications;

/// <summary>Connects the browser notification facade to the authenticated in-process device API.</summary>
public sealed class GatewayNotificationPlatformSettings : INotificationPlatformSettings
{
    private readonly IJSRuntime _jsRuntime;
    private readonly INotificationFeedClient _feed;

    public GatewayNotificationPlatformSettings(IJSRuntime jsRuntime, INotificationFeedClient feed)
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
    }

    public async Task<NotificationPlatformSettingsSnapshot> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await _jsRuntime.InvokeAsync<JsonElement>(
                "swiftAidNotifications.getState",
                cancellationToken).ConfigureAwait(false);
            return new NotificationPlatformSettingsSnapshot(
                ParsePermission(state),
                state.TryGetProperty("pushEnabled", out var pushEnabled) && pushEnabled.ValueKind == JsonValueKind.True,
                state.TryGetProperty("gentleSoundEnabled", out var soundEnabled) && soundEnabled.ValueKind == JsonValueKind.True);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new NotificationPlatformSettingsSnapshot(
                NotificationPermissionState.Unknown,
                false,
                false);
        }
        catch (Exception exception) when (IsBrowserInteropFailure(exception))
        {
            return new NotificationPlatformSettingsSnapshot(
                NotificationPermissionState.Unknown,
                false,
                false);
        }
    }

    public async Task<NotificationPermissionState> GetPermissionStatusAsync(
        CancellationToken cancellationToken = default) =>
        (await GetStateAsync(cancellationToken).ConfigureAwait(false)).Permission;

    public async Task<NotificationPlatformResult> EnableAsync(
        bool playGentleSound,
        CancellationToken cancellationToken = default)
    {
        var callback = new TokenRegistrationCallback(_feed, cancellationToken);
        using var callbackReference = DotNetObjectReference.Create(callback);
        try
        {
            var enabled = await _jsRuntime.InvokeAsync<bool>(
                "swiftAidNotifications.enable",
                cancellationToken,
                args: [null, playGentleSound, callbackReference]).ConfigureAwait(false);
            if (enabled)
            {
                return NotificationPlatformResult.Enabled;
            }

            var currentPermission = await GetPermissionStatusAsync(cancellationToken).ConfigureAwait(false);
            return currentPermission switch
            {
                NotificationPermissionState.NotSupported => NotificationPlatformResult.NotSupported,
                NotificationPermissionState.Denied => NotificationPlatformResult.PermissionDenied,
                _ => callback.Result switch
                {
                    PushDeviceRegistrationStatus.NotAuthenticated => NotificationPlatformResult.NotAuthenticated,
                    PushDeviceRegistrationStatus.Unavailable => NotificationPlatformResult.Unavailable,
                    PushDeviceRegistrationStatus.Invalid => NotificationPlatformResult.Unavailable,
                    _ => NotificationPlatformResult.Unavailable
                }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return NotificationPlatformResult.Unavailable;
        }
        catch (Exception exception) when (IsBrowserInteropFailure(exception))
        {
            return NotificationPlatformResult.Unavailable;
        }
    }

    public async Task<NotificationPlatformResult> SetGentleSoundAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (state.Permission == NotificationPermissionState.NotSupported)
        {
            return NotificationPlatformResult.NotSupported;
        }

        if (!state.PushEnabled)
        {
            return NotificationPlatformResult.Disabled;
        }

        try
        {
            var callback = new TokenRegistrationCallback(_feed, cancellationToken);
            using var callbackReference = DotNetObjectReference.Create(callback);
            var updated = await _jsRuntime.InvokeAsync<bool>(
                "swiftAidNotifications.setGentleSound",
                cancellationToken,
                enabled,
                callbackReference).ConfigureAwait(false);
            if (updated)
            {
                return NotificationPlatformResult.Enabled;
            }

            if (callback.Result == PushDeviceRegistrationStatus.NotAuthenticated)
            {
                return NotificationPlatformResult.NotAuthenticated;
            }

            return await GetPermissionStatusAsync(cancellationToken).ConfigureAwait(false) ==
                NotificationPermissionState.NotSupported
                ? NotificationPlatformResult.NotSupported
                : NotificationPlatformResult.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return NotificationPlatformResult.Unavailable;
        }
        catch (Exception exception) when (IsBrowserInteropFailure(exception))
        {
            return NotificationPlatformResult.Unavailable;
        }
    }

    public async Task<bool> TryPlayForegroundSoundAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default)
    {
        if (occurrenceId == Guid.Empty)
        {
            return false;
        }

        try
        {
            return await _jsRuntime.InvokeAsync<bool>(
                "swiftAidNotifications.tryPlayForegroundSound",
                cancellationToken,
                occurrenceId.ToString("D")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (IsBrowserInteropFailure(exception))
        {
            return false;
        }
    }

    public async Task<NotificationPlatformResult> DisableAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var installationIdValue = await _jsRuntime.InvokeAsync<string?>(
                "swiftAidNotifications.getInstallationId",
                cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(installationIdValue, out var installationId) || installationId == Guid.Empty)
            {
                return NotificationPlatformResult.Unavailable;
            }

            var removal = await _feed.RemoveDeviceAsync(installationId, cancellationToken).ConfigureAwait(false);
            if (removal.Status == PushDeviceRemovalStatus.NotAuthenticated)
            {
                return NotificationPlatformResult.NotAuthenticated;
            }

            // A missing/foreign installation is not evidence that this server registration was
            // revoked; keep the browser token and installation ID so the user can retry safely.
            if (removal.Status != PushDeviceRemovalStatus.Removed)
            {
                return NotificationPlatformResult.Unavailable;
            }

            var disabled = await _jsRuntime.InvokeAsync<bool>(
                "swiftAidNotifications.disable",
                cancellationToken).ConfigureAwait(false);
            return disabled ? NotificationPlatformResult.Disabled : NotificationPlatformResult.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return NotificationPlatformResult.Unavailable;
        }
        catch (Exception exception) when (IsBrowserInteropFailure(exception))
        {
            return NotificationPlatformResult.Unavailable;
        }
    }

    private static NotificationPermissionState ParsePermission(JsonElement state)
    {
        if (!state.TryGetProperty("permission", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return NotificationPermissionState.Unknown;
        }

        return Enum.TryParse<NotificationPermissionState>(value.GetString(), ignoreCase: false, out var permission)
            ? permission
            : NotificationPermissionState.Unknown;
    }

    private static bool IsBrowserInteropFailure(Exception exception) =>
        exception is JSException or InvalidOperationException or ObjectDisposedException or ArgumentException;

    private sealed class TokenRegistrationCallback(
        INotificationFeedClient feed,
        CancellationToken cancellationToken)
    {
        private readonly INotificationFeedClient _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        private readonly CancellationToken _cancellationToken = cancellationToken;

        public PushDeviceRegistrationStatus Result { get; private set; } = PushDeviceRegistrationStatus.Unavailable;

        [JSInvokable("RegisterTokenAsync")]
        public async Task<bool> RegisterTokenAsync(
            string installationId,
            string token,
            bool soundEnabled)
        {
            if (!Guid.TryParse(installationId, out var parsedId) || parsedId == Guid.Empty ||
                string.IsNullOrWhiteSpace(token))
            {
                Result = PushDeviceRegistrationStatus.Invalid;
                return false;
            }

            try
            {
                var response = await _feed.RegisterDeviceAsync(
                    parsedId,
                    PushDevicePlatform.Web,
                    token,
                    soundEnabled,
                    _cancellationToken).ConfigureAwait(false);
                Result = response.Status;
                return response.Status == PushDeviceRegistrationStatus.Registered;
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Result = PushDeviceRegistrationStatus.Unavailable;
                return false;
            }
        }
    }
}
