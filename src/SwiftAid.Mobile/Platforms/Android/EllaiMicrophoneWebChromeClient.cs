using Android.Webkit;

namespace SwiftAid.Mobile.Platforms.Android;

/// <summary>Allows the Android Blazor WebView to honor its microphone request
/// only after MAUI has already obtained the app-level microphone permission.</summary>
internal sealed class EllaiMicrophoneWebChromeClient : WebChromeClient
{
    public override async void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is null)
        {
            return;
        }

        var resources = request.GetResources() ?? Array.Empty<string>();
        if (resources.Length != 1 || !string.Equals(
                resources[0], PermissionRequest.ResourceAudioCapture, StringComparison.OrdinalIgnoreCase))
        {
            await DenyOnMainThreadAsync(request).ConfigureAwait(false);
            return;
        }

        try
        {
            var granted = await Permissions.CheckStatusAsync<Permissions.Microphone>().ConfigureAwait(false)
                == PermissionStatus.Granted;
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (granted)
                {
                    request.Grant([PermissionRequest.ResourceAudioCapture]);
                }
                else
                {
                    request.Deny();
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            await DenyOnMainThreadAsync(request).ConfigureAwait(false);
        }
    }

    private static Task DenyOnMainThreadAsync(PermissionRequest request) =>
        MainThread.InvokeOnMainThreadAsync(request.Deny);
}
