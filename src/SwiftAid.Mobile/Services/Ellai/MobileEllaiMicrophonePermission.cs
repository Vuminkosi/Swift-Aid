using SwiftAid.UI.Ellai;

namespace SwiftAid.Mobile.Services.Ellai;

/// <summary>Requests OS microphone access before the shared Blazor WebView
/// starts a transient conversational capture.</summary>
public sealed class MobileEllaiMicrophonePermission : IEllaiMicrophonePermission
{
    public async Task<bool> RequestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>().ConfigureAwait(false);
        if (status != PermissionStatus.Granted)
        {
            status = await MainThread.InvokeOnMainThreadAsync(
                () => Permissions.RequestAsync<Permissions.Microphone>()).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return status == PermissionStatus.Granted;
    }
}
