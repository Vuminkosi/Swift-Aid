using Android.Webkit;

namespace SwiftAid.Mobile.Platforms.Android;

/// <summary>Allows the Android Blazor WebView to honor its microphone request
/// only after MAUI has already obtained the app-level microphone permission.</summary>
internal sealed class EllaiMicrophoneWebChromeClient : WebChromeClient
{
    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is null)
        {
            return;
        }

        var resources = request.GetResources() ?? Array.Empty<string>();
        if (resources.Any(resource => string.Equals(
                resource, PermissionRequest.ResourceAudioCapture, StringComparison.OrdinalIgnoreCase)))
        {
            var granted = Permissions.CheckStatusAsync<Permissions.Microphone>()
                .GetAwaiter().GetResult() == PermissionStatus.Granted;
            if (granted)
            {
                request.Grant([PermissionRequest.ResourceAudioCapture]);
            }
            else
            {
                request.Deny();
            }
            return;
        }

        request.Deny();
    }
}
