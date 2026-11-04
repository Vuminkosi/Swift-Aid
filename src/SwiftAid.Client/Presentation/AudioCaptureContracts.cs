using SwiftAid.Client.Intake;

namespace SwiftAid.Client.Presentation;

public enum AudioCaptureStatus
{
    Idle,
    Recording,
    ReadyToSave,
    Saved,
    Playing,
    PermissionDenied,
    Unsupported,
    Failed
}

public sealed record AudioCaptureState(
    AudioCaptureStatus Status,
    TimeSpan Elapsed,
    string? Message = null,
    bool TemporaryCleanupPending = false);

public sealed record AudioDraftSaveResult(bool Saved, GuestAudioAsset? Asset, string? Message);

public interface IAudioCaptureService
{
    event EventHandler<AudioCaptureState>? StateChanged;

    AudioCaptureState CurrentState { get; }

    Task<AudioCaptureState> StartAsync(CancellationToken cancellationToken = default);

    Task<AudioCaptureState> StopAsync(CancellationToken cancellationToken = default);

    Task<AudioDraftSaveResult> SaveToDraftAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default);

    Task<AudioCaptureState> RetryAsync(CancellationToken cancellationToken = default);

    Task<AudioCaptureState> RetryTemporaryCleanupAsync(CancellationToken cancellationToken = default);
}

public interface IAudioPlaybackService
{
    Task<AudioCaptureState> PlaySavedAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default);

    Task<AudioCaptureState> PlayAsync(CancellationToken cancellationToken = default);

    Task<AudioCaptureState> StopPlaybackAsync(CancellationToken cancellationToken = default);
}
