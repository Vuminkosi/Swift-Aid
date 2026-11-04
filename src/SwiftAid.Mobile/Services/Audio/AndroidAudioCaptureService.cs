using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using System.Security.Cryptography;

namespace SwiftAid.Mobile.Services.Audio;

#if ANDROID

/// <summary>
/// Android implementation backed by the native microphone and media player. Recording bytes
/// are moved into the encrypted guest store as soon as capture stops; the temporary source is
/// held in app-private cache. Failed cache cleanup remains tracked and is reported for retry.
/// </summary>
public sealed class AndroidAudioCaptureService : IAudioCaptureService, IAudioPlaybackService, IDisposable
{
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromMilliseconds(500);
    private readonly IGuestAudioStore _audioStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Android.Media.MediaRecorder? _recorder;
    private Android.Media.MediaPlayer? _player;
    private string? _recordingPath;
    private string? _playbackPath;
    private bool _recordingCleanupPending;
    private bool _playbackCleanupPending;
    private byte[]? _recordingBytes;
    private bool _playbackIsSavedOriginal;
    private long? _startedAtTimestamp;
    private long _recordingTimerGeneration;
    private Timer? _stateTimer;
    private AudioCaptureState _state = new(AudioCaptureStatus.Idle, TimeSpan.Zero);
    private int _disposeStarted;
    private volatile bool _disposed;
    private bool TemporaryCleanupPending => _recordingCleanupPending || _playbackCleanupPending;

    public AndroidAudioCaptureService(IGuestAudioStore audioStore, TimeProvider? timeProvider = null)
    {
        _audioStore = audioStore ?? throw new ArgumentNullException(nameof(audioStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<AudioCaptureState>? StateChanged;

    public AudioCaptureState CurrentState => _state;

    public async Task<AudioCaptureState> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state.Status == AudioCaptureStatus.Recording)
            {
                return _state;
            }

            var playbackCleaned = await StopPlaybackCoreAsync().ConfigureAwait(false);
            var recordingCleaned = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
            if (!playbackCleaned || !recordingCleaned)
            {
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.Failed,
                    _state.Elapsed,
                    "A temporary audio file could not be removed. Keep this screen open and try again."));
            }

            ClearRecordingBytes();
            _playbackIsSavedOriginal = false;
            _startedAtTimestamp = null;
            PermissionStatus permission;
            try
            {
                permission = await MainThread.InvokeOnMainThreadAsync(
                    () => Permissions.RequestAsync<Permissions.Microphone>()).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.Failed,
                    TimeSpan.Zero,
                    "Microphone permission could not be checked. You can still type or save observations."));
            }
            if (permission != PermissionStatus.Granted)
            {
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.PermissionDenied,
                    TimeSpan.Zero,
                    "Microphone permission was not granted. You can still type or save observations."));
            }

            var path = Path.Combine(FileSystem.CacheDirectory, $"swiftaid-voice-{Guid.NewGuid():N}.m4a");
            _recordingPath = path;
            try
            {
                _recorder = CreateRecorder();
                _recorder.SetAudioSource(Android.Media.AudioSource.Mic);
                _recorder.SetOutputFormat(Android.Media.OutputFormat.Mpeg4);
                _recorder.SetAudioEncoder(Android.Media.AudioEncoder.Aac);
                _recorder.SetAudioEncodingBitRate(64_000);
                _recorder.SetAudioSamplingRate(44_100);
                _recorder.SetMaxFileSize(10 * 1024 * 1024);
                _recorder.SetOutputFile(path);
                _recorder.Prepare();
                _recorder.Start();
                _startedAtTimestamp = _timeProvider.GetTimestamp();
                var timerGeneration = Interlocked.Increment(ref _recordingTimerGeneration);
                _stateTimer = new Timer(_ => PublishElapsed(timerGeneration), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
                return Publish(new AudioCaptureState(AudioCaptureStatus.Recording, TimeSpan.Zero));
            }
            catch (Exception)
            {
                ReleaseRecorder();
                var cleaned = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.Failed,
                    TimeSpan.Zero,
                    cleaned
                        ? "Couldn’t start recording. Try again or continue by typing."
                        : "Couldn’t start recording, and a temporary file remains. Keep this screen open and try again."));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioCaptureState> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state.Status != AudioCaptureStatus.Recording || _recorder is null || _recordingPath is null)
            {
                return _state;
            }

            StopRecordingTimer();
            var elapsed = GetElapsed();
            try
            {
                _recorder.Stop();
                ReleaseRecorder();
                var file = new FileInfo(_recordingPath);
                if (elapsed < MinimumRecordingDuration || !file.Exists || file.Length == 0 || file.Length > 10 * 1024 * 1024)
                {
                    var cleaned = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
                    return Publish(new AudioCaptureState(
                        AudioCaptureStatus.Failed,
                        elapsed,
                        cleaned
                            ? "The recording was too short or could not be read. Try again or continue by typing."
                            : "The recording could not be used, and a temporary file remains. Keep this screen open and try again."));
                }

                _recordingBytes = await File.ReadAllBytesAsync(_recordingPath, cancellationToken).ConfigureAwait(false);
                var temporaryFileRemoved = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.ReadyToSave,
                    elapsed,
                    temporaryFileRemoved
                        ? null
                        : "Recording is ready, but a temporary copy could not be removed. Keep this screen open and try again."));
            }
            catch (Exception)
            {
                ReleaseRecorder();
                var cleaned = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
                ClearRecordingBytes();
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.Failed,
                    elapsed,
                    cleaned
                        ? "The recording could not be finished. Try again or continue by typing."
                        : "The recording could not be finished, and a temporary file remains. Keep this screen open and try again."));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioDraftSaveResult> SaveToDraftAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (clientSubmissionId == Guid.Empty || attachmentId == Guid.Empty)
        {
            return new AudioDraftSaveResult(false, null, "Save the report draft before adding a recording.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recordingBytes is not { Length: > 0 } bytes || _state.Status != AudioCaptureStatus.ReadyToSave)
            {
                return new AudioDraftSaveResult(false, null, "Stop a recording before saving it to the report draft.");
            }

            var durationMilliseconds = Math.Clamp((int)_state.Elapsed.TotalMilliseconds, 1, (int)MaximumDuration.TotalMilliseconds);
            try
            {
                await using var content = new MemoryStream(bytes, writable: false);
                var asset = await _audioStore.SaveOriginalAsync(
                    clientSubmissionId,
                    attachmentId,
                    "audio/mp4",
                    durationMilliseconds,
                    content,
                    cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(bytes);
                _recordingBytes = null;
                _playbackIsSavedOriginal = false;
                var temporaryFileRemoved = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
                var savedMessage = temporaryFileRemoved
                    ? "Original recording saved privately with this draft on the device. It has not been sent."
                    : "Recording saved with this draft, but a temporary copy remains on this device. Keep this screen open and try again to remove it.";
                Publish(new AudioCaptureState(
                    AudioCaptureStatus.Saved,
                    _state.Elapsed,
                    savedMessage));
                return new AudioDraftSaveResult(true, asset, savedMessage);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or KeyNotFoundException or GuestSubmissionStoreUnavailableException)
            {
                return new AudioDraftSaveResult(false, null, "The recording could not be saved to this draft. Keep this screen open and try again, or continue by typing.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioCaptureState> PlayAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _playbackIsSavedOriginal = false;
            var bytes = _recordingBytes;
            if (bytes is not { Length: > 0 })
            {
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.Failed,
                    _state.Elapsed,
                    "The recording is unavailable for playback. Record again or continue without audio."));
            }

            return await StartPlaybackCoreAsync(bytes, isSavedOriginal: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var cleaned = await StopPlaybackCoreAsync().ConfigureAwait(false);
            return Publish(new AudioCaptureState(
                AudioCaptureStatus.Failed,
                _state.Elapsed,
                cleaned
                    ? "Playback could not start. Try again or continue without audio."
                    : "Playback could not start, and a temporary copy remains. Keep this screen open and try again."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioCaptureState> PlaySavedAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (clientSubmissionId == Guid.Empty || attachmentId == Guid.Empty)
        {
            return Publish(new AudioCaptureState(
                AudioCaptureStatus.Failed,
                TimeSpan.Zero,
                "The saved recording could not be identified. Reopen the report and try again."));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _playbackIsSavedOriginal = false;
            var bytes = await _audioStore.ReadOriginalAsync(clientSubmissionId, attachmentId, cancellationToken).ConfigureAwait(false);
            try
            {
                return await StartPlaybackCoreAsync(bytes, isSavedOriginal: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var cleaned = await StopPlaybackCoreAsync().ConfigureAwait(false);
            return Publish(new AudioCaptureState(
                AudioCaptureStatus.Failed,
                TimeSpan.Zero,
                cleaned
                    ? "The saved recording could not be opened. Your original remains saved with this draft."
                    : "The saved recording could not be opened, and a temporary copy remains. Keep this screen open and try again."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioCaptureState> StopPlaybackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await StopPlaybackCoreAsync().ConfigureAwait(false))
            {
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.Failed,
                    _state.Elapsed,
                    "The playback copy could not be removed. Keep this screen open and try again."));
            }

            var wasSavedOriginal = _playbackIsSavedOriginal;
            _playbackIsSavedOriginal = false;
            return Publish(new AudioCaptureState(
                wasSavedOriginal
                    ? AudioCaptureStatus.Saved
                    : _recordingBytes is { Length: > 0 }
                        ? AudioCaptureStatus.ReadyToSave
                        : AudioCaptureStatus.Idle,
                _state.Elapsed,
                wasSavedOriginal
                    ? "Original recording remains saved privately with this draft on the device. It has not been sent."
                    : null));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops and discards any unsaved capture when the report screen is left.</summary>
    public Task<AudioCaptureState> CancelCaptureAsync(CancellationToken cancellationToken = default) =>
        ResetCaptureSessionAsync(cancellationToken);

    /// <summary>Clears transient capture state before the user starts a separate report.</summary>
    public Task<AudioCaptureState> ResetForNewReportAsync(CancellationToken cancellationToken = default) =>
        ResetCaptureSessionAsync(cancellationToken);

    private async Task<AudioCaptureState> ResetCaptureSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopRecordingTimer();
            var playbackCleaned = await StopPlaybackCoreAsync().ConfigureAwait(false);
            if (_recorder is { } recorder)
            {
                try
                {
                    recorder.Stop();
                }
                catch (Exception)
                {
                }
                finally
                {
                    ReleaseRecorder();
                }
            }

            var recordingCleaned = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
            ClearRecordingBytes();
            _startedAtTimestamp = null;
            _playbackIsSavedOriginal = false;
            if (!playbackCleaned || !recordingCleaned)
            {
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.Failed,
                    TimeSpan.Zero,
                    "A temporary audio file could not be removed. Keep this screen open and try again."));
            }

            return Publish(new AudioCaptureState(AudioCaptureStatus.Idle, TimeSpan.Zero));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioCaptureState> RetryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state.Status == AudioCaptureStatus.Recording)
            {
                return _state;
            }

            var playbackCleaned = await StopPlaybackCoreAsync().ConfigureAwait(false);
            var recordingCleaned = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
            if (!playbackCleaned || !recordingCleaned)
            {
                return Publish(new AudioCaptureState(
                    AudioCaptureStatus.Failed,
                    _state.Elapsed,
                    "A temporary audio file could not be removed. Keep this screen open and try again."));
            }

            ClearRecordingBytes();
            _playbackIsSavedOriginal = false;
            _startedAtTimestamp = null;
        }
        finally
        {
            _gate.Release();
        }

        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioCaptureState> RetryTemporaryCleanupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TemporaryCleanupPending || _recorder is not null || _player is not null ||
                _state.Status is AudioCaptureStatus.Recording or AudioCaptureStatus.Playing)
            {
                return _state;
            }

            var recordingCleaned = await DeleteTemporaryRecordingAsync().ConfigureAwait(false);
            var playbackCleaned = await DeletePlaybackFileAsync().ConfigureAwait(false);
            var message = recordingCleaned && playbackCleaned
                ? "Temporary audio copy removed."
                : "A temporary audio copy could not be removed. Try again.";
            return Publish(_state with { Message = message });
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            StopRecordingTimer();
            if (_recorder is { } recorder)
            {
                try
                {
                    recorder.Stop();
                }
                catch (Exception)
                {
                }

                ReleaseRecorder();
            }

            var playbackCleaned = StopPlaybackCoreAsync().GetAwaiter().GetResult();
            var recordingCleaned = DeleteTemporaryRecordingAsync().GetAwaiter().GetResult();
            if (!playbackCleaned || !recordingCleaned)
            {
                System.Diagnostics.Debug.WriteLine("A temporary SwiftAid audio file could not be removed during service disposal; its path remains tracked by the audio service.");
            }

            ClearRecordingBytes();
            _startedAtTimestamp = null;
            _playbackIsSavedOriginal = false;
            _state = new AudioCaptureState(
                AudioCaptureStatus.Idle,
                TimeSpan.Zero,
                TemporaryCleanupPending: TemporaryCleanupPending);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static Android.Media.MediaRecorder CreateRecorder()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return new Android.Media.MediaRecorder(Android.App.Application.Context);
        }

        return new Android.Media.MediaRecorder();
    }

    private void PublishElapsed(long timerGeneration)
    {
        try
        {
            if (!_gate.Wait(0))
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var stopAtMaximum = false;
        try
        {
            if (_disposed || timerGeneration != Interlocked.Read(ref _recordingTimerGeneration) ||
                _state.Status != AudioCaptureStatus.Recording)
            {
                return;
            }

            var elapsed = GetElapsed();
            if (elapsed >= MaximumDuration)
            {
                stopAtMaximum = true;
            }
            else
            {
                Publish(new AudioCaptureState(AudioCaptureStatus.Recording, elapsed));
            }
        }
        finally
        {
            _gate.Release();
        }

        if (stopAtMaximum)
        {
            _ = StopAtMaximumDurationAsync();
        }
    }

    private async Task StopAtMaximumDurationAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StopRecordingTimer()
    {
        Interlocked.Increment(ref _recordingTimerGeneration);
        _stateTimer?.Dispose();
        _stateTimer = null;
    }

    private TimeSpan GetElapsed() => _startedAtTimestamp is { } started
        ? _timeProvider.GetElapsedTime(started)
        : TimeSpan.Zero;

    private async Task<AudioCaptureState> StartPlaybackCoreAsync(
        byte[] bytes,
        bool isSavedOriginal,
        CancellationToken cancellationToken)
    {
        if (!await StopPlaybackCoreAsync().ConfigureAwait(false))
        {
            return Publish(new AudioCaptureState(
                AudioCaptureStatus.Failed,
                _state.Elapsed,
                "A playback copy could not be removed. Keep this screen open and try again."));
        }

        var path = Path.Combine(FileSystem.CacheDirectory, $"swiftaid-playback-{Guid.NewGuid():N}.m4a");
        _playbackPath = path;
        Android.Media.MediaPlayer? player = null;
        try
        {
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            var activePlayer = new Android.Media.MediaPlayer();
            player = activePlayer;
            activePlayer.SetDataSource(path);
            activePlayer.Prepared += (_, _) =>
            {
                if (ReferenceEquals(_player, activePlayer))
                {
                    activePlayer.Start();
                }
            };
            activePlayer.Completion += (_, _) =>
            {
                if (ReferenceEquals(_player, activePlayer))
                {
                    _ = StopPlaybackAsync();
                }
            };
            activePlayer.Error += (_, _) =>
            {
                if (ReferenceEquals(_player, activePlayer))
                {
                    _ = StopPlaybackAsync();
                }
            };
            activePlayer.PrepareAsync();
            _player = activePlayer;
            _playbackIsSavedOriginal = isSavedOriginal;
            return Publish(new AudioCaptureState(AudioCaptureStatus.Playing, _state.Elapsed));
        }
        catch
        {
            if (player is not null)
            {
                try
                {
                    player.Release();
                }
                catch (Exception)
                {
                }

                try
                {
                    player.Dispose();
                }
                catch (Exception)
                {
                }
            }

            var cleaned = await DeletePlaybackFileAsync().ConfigureAwait(false);
            return Publish(new AudioCaptureState(
                AudioCaptureStatus.Failed,
                _state.Elapsed,
                cleaned
                    ? "Playback could not start. Try again or continue without audio."
                    : "Playback could not start, and a temporary copy remains. Keep this screen open and try again."));
        }
    }

    private AudioCaptureState Publish(AudioCaptureState state)
    {
        state = state with { TemporaryCleanupPending = TemporaryCleanupPending };
        _state = state;
        MainThread.BeginInvokeOnMainThread(() => StateChanged?.Invoke(this, state));
        return state;
    }

    private Task<bool> DeleteTemporaryRecordingAsync() =>
        Task.FromResult(TryDeleteTrackedFile(ref _recordingPath, ref _recordingCleanupPending));

    private Task<bool> DeletePlaybackFileAsync() =>
        Task.FromResult(TryDeleteTrackedFile(ref _playbackPath, ref _playbackCleanupPending));

    private void ClearRecordingBytes()
    {
        if (_recordingBytes is { } bytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        _recordingBytes = null;
    }

    private async Task<bool> StopPlaybackCoreAsync()
    {
        if (_player is { } player)
        {
            _player = null;
            try
            {
                if (player.IsPlaying)
                {
                    player.Stop();
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    player.Release();
                }
                catch (Exception)
                {
                }

                try
                {
                    player.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        return await DeletePlaybackFileAsync().ConfigureAwait(false);
    }

    private void ReleaseRecorder()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null)
        {
            return;
        }

        try
        {
            recorder.Reset();
        }
        catch (Exception)
        {
        }
        finally
        {
            try
            {
                recorder.Release();
            }
            catch (Exception)
            {
            }

            try
            {
                recorder.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool TryDeleteTrackedFile(ref string? trackedPath, ref bool cleanupPending)
    {
        if (trackedPath is not { } path)
        {
            cleanupPending = false;
            return true;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            cleanupPending = true;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            cleanupPending = true;
            return false;
        }

        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            trackedPath = null;
            cleanupPending = false;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            trackedPath = null;
            cleanupPending = false;
            return true;
        }
        catch (IOException)
        {
            cleanupPending = true;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            cleanupPending = true;
            return false;
        }
    }
}

#endif
