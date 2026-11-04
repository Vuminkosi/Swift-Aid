using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Mobile.Services.Audio;

/// <summary>Explicit capability result on targets without the Android recorder implementation.</summary>
public sealed class UnsupportedMobileAudioCaptureService : IAudioCaptureService, IAudioPlaybackService
{
    private static readonly AudioCaptureState UnsupportedState = new(
        AudioCaptureStatus.Unsupported,
        TimeSpan.Zero,
        "Original voice recording is unavailable on this device. You can continue by typing or saving observations.");

    public event EventHandler<AudioCaptureState>? StateChanged
    {
        add { }
        remove { }
    }

    public AudioCaptureState CurrentState => UnsupportedState;

    public Task<AudioCaptureState> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsupportedState);

    public Task<AudioCaptureState> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsupportedState);

    public Task<AudioCaptureState> PlayAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsupportedState);

    public Task<AudioCaptureState> PlaySavedAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default) => Task.FromResult(UnsupportedState);

    public Task<AudioCaptureState> StopPlaybackAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsupportedState);

    public Task<AudioCaptureState> RetryAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsupportedState);

    public Task<AudioCaptureState> RetryTemporaryCleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UnsupportedState);
    }

    public Task<AudioDraftSaveResult> SaveToDraftAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AudioDraftSaveResult(false, null, UnsupportedState.Message));
}


