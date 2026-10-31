using Android.Media;
using Android.Media.Audiofx;
using Android.OS;
using System.Diagnostics;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Mobile.Platforms.Android;

/// <summary>Transient Ellai live capture using Android's voice communication
/// source. Effects attach to this AudioRecord session only; guest evidence
/// recording continues to use its independent encrypted MediaRecorder path.</summary>
public sealed class AndroidEllaiNativeVoiceCapture : IEllaiNativeVoiceCapture, IAsyncDisposable
{
    private const int TargetSampleRate = 24_000;
    private const double BargeInRmsThreshold = 0.025;
    private static readonly TimeSpan BargeInConfirmation = TimeSpan.FromMilliseconds(180);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioRecord? _record;
    private global::Android.Media.IAudioRoutingOnRoutingChangedListener? _routingChangedListener;
    private AcousticEchoCanceler? _echoCanceler;
    private NoiseSuppressor? _noiseSuppressor;
    private AutomaticGainControl? _gainControl;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private AudioManager? _audioManager;
    private AudioFocusRequestClass? _audioFocusRequest;
    private FocusChangeListener? _focusChangeListener;
    private Mode _previousMode;
    private bool _disposed;

    public bool IsAvailable => !_disposed;
    public event Action<string>? Interrupted;
    public event Action? SpeechDetected;
    public event Action? RouteChanged;

    public async Task<string> StartAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onPcmChunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onPcmChunk);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_record is not null)
            {
                throw new InvalidOperationException("Ellai native microphone capture is already active.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _audioManager = (AudioManager?)global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.AudioService);
            if (_audioManager is null)
            {
                throw new InvalidOperationException("Android audio services are unavailable.");
            }

            _previousMode = _audioManager.Mode;
            _audioManager.Mode = Mode.InCommunication;

            try
            {
                RequestAudioFocus(_audioManager);
                _record = TryCreateAudioRecord(TargetSampleRate) ?? TryCreateAudioRecord(48_000);

                if (_record is null || _record.State != State.Initialized)
                {
                    throw new InvalidOperationException("Android could not initialize voice capture.");
                }

                var status = EnableSessionEffects(_record.AudioSessionId);
                _record.StartRecording();
                if (_record.RecordingState != RecordState.Recording)
                {
                    throw new InvalidOperationException("Android did not start voice capture.");
                }

                _routingChangedListener = new RoutingChangeListener(() => RouteChanged?.Invoke());
                _record.AddOnRoutingChangedListener(_routingChangedListener, null);

                _captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var activeRecord = _record;
                var activeCancellation = _captureCts.Token;
                _captureTask = Task.Run(async () =>
                {
                    try
                    {
                        await ReadCaptureLoopAsync(activeRecord, activeCancellation, onPcmChunk)
                            .ConfigureAwait(false);
                    }
                    catch (System.OperationCanceledException) when (activeCancellation.IsCancellationRequested)
                    {
                    }
                    catch when (activeCancellation.IsCancellationRequested)
                    {
                    }
                    catch
                    {
                        Interrupted?.Invoke(
                            "Android voice capture stopped unexpectedly. Try again or type your message.");
                        throw;
                    }
                }, CancellationToken.None);
                return status;
            }
            catch
            {
                try
                {
                    await ReleaseCaptureCoreAsync().ConfigureAwait(false);
                }
                finally
                {
                    RestoreAudioMode();
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseCaptureCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            RestoreAudioMode();
            _gate.Release();
        }
    }

    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        Interrupted?.Invoke("Voice capture stopped when the app moved to the background. Try again or type your message.");
    }

    private void RequestAudioFocus(AudioManager audioManager)
    {
        var attributes = new AudioAttributes.Builder()!
            .SetUsage(AudioUsageKind.VoiceCommunication)!
            .SetContentType(AudioContentType.Speech)!
            .Build() ?? throw new InvalidOperationException("Android audio attributes are unavailable.");
        _focusChangeListener = new FocusChangeListener(OnAudioFocusChanged);

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            _audioFocusRequest = new AudioFocusRequestClass.Builder(AudioFocus.GainTransient)!
                .SetAudioAttributes(attributes)!
                .SetOnAudioFocusChangeListener(_focusChangeListener)!
                .Build();
            if (_audioFocusRequest is null
                || audioManager.RequestAudioFocus(_audioFocusRequest) != AudioFocusRequest.Granted)
            {
                throw new InvalidOperationException("Android audio focus is unavailable.");
            }
        }
        else if (audioManager.RequestAudioFocus(
                     _focusChangeListener, global::Android.Media.Stream.VoiceCall, AudioFocus.GainTransient)
                 != AudioFocusRequest.Granted)
        {
            throw new InvalidOperationException("Android audio focus is unavailable.");
        }
    }

    private void OnAudioFocusChanged(AudioFocus focusChange)
    {
        if ((int)focusChange is -1 or -2 or -3)
        {
            _ = SuspendForFocusLossAsync();
        }
    }

    private async Task SuspendForFocusLossAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
            Interrupted?.Invoke("Another app needs audio focus. Voice capture stopped; try again or type your message.");
        }
        catch
        {
            // The owning UI will surface capture failure when the user ends the turn.
        }
    }

    private static AudioRecord? TryCreateAudioRecord(int sampleRate)
    {
        var minimumBytes = AudioRecord.GetMinBufferSize(
            sampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        if (minimumBytes <= 0)
        {
            return null;
        }

        var frameBytes = sampleRate / 50 * sizeof(short);
        try
        {
            var record = new AudioRecord(
                AudioSource.VoiceCommunication,
                sampleRate,
                ChannelIn.Mono,
                Encoding.Pcm16bit,
                Math.Max(minimumBytes, frameBytes * 4));
            if (record.State == State.Initialized)
            {
                return record;
            }

            record.Release();
            record.Dispose();
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string EnableSessionEffects(int audioSessionId)
    {
        _echoCanceler = AcousticEchoCanceler.IsAvailable
            ? AcousticEchoCanceler.Create(audioSessionId)
            : null;
        _noiseSuppressor = NoiseSuppressor.IsAvailable
            ? NoiseSuppressor.Create(audioSessionId)
            : null;
        _gainControl = AutomaticGainControl.IsAvailable
            ? AutomaticGainControl.Create(audioSessionId)
            : null;

        Enable(_echoCanceler);
        Enable(_noiseSuppressor);
        Enable(_gainControl);

        return $"Android voice communication capture; echo cancellation {StateOf(_echoCanceler)}, "
            + $"noise suppression {StateOf(_noiseSuppressor)}, gain control {StateOf(_gainControl)}.";
    }

    private static void Enable(AudioEffect? effect)
    {
        if (effect is not null && !effect.Enabled)
        {
            effect.SetEnabled(true);
        }
    }

    private static string StateOf(AudioEffect? effect) => effect is null
        ? "unavailable"
        : effect.Enabled ? "on" : "off";

    private async Task ReadCaptureLoopAsync(
        AudioRecord record,
        CancellationToken cancellationToken,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onPcmChunk)
    {
        var inputRate = record.SampleRate;
        var samplesPerChunk = inputRate / 50;
        var samples = new short[samplesPerChunk];
        var lastVoiceActivityTimestamp = Stopwatch.GetTimestamp();
        var bargeInDetector = new EllaiSpeechConfirmation(BargeInRmsThreshold, BargeInConfirmation);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = record.Read(samples, 0, samples.Length);
                if (read < 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    throw new InvalidOperationException($"Android audio capture failed with status {read}.");
                }
                if (read == 0)
                {
                    continue;
                }

                var voiceLevel = MeasureRms(samples.AsSpan(0, read));
                if (voiceLevel >= 0.025)
                {
                    lastVoiceActivityTimestamp = Stopwatch.GetTimestamp();
                }
                else if (Stopwatch.GetElapsedTime(lastVoiceActivityTimestamp) >= TimeSpan.FromMinutes(1))
                {
                    Interrupted?.Invoke(
                        "No speech detected. The microphone stopped; you can try again or type instead.");
                    break;
                }

                if (bargeInDetector.Observe(
                        voiceLevel,
                        TimeSpan.FromSeconds(read / (double)inputRate),
                        _echoCanceler?.Enabled == true))
                {
                    try
                    {
                        SpeechDetected?.Invoke();
                    }
                    catch (Exception)
                    {
                        // A UI listener must not stop the native capture loop.
                    }
                }

                var pcm = ConvertToTargetRate(samples.AsSpan(0, read), inputRate);
                try
                {
                    await onPcmChunk(pcm, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Array.Clear(pcm);
                }
            }
        }
        finally
        {
            Array.Clear(samples);
        }
    }

    private static double MeasureRms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sumSquares = 0;
        foreach (var sample in samples)
        {
            var normalized = sample / 32768d;
            sumSquares += normalized * normalized;
        }
        return Math.Sqrt(sumSquares / samples.Length);
    }

    private static byte[] ConvertToTargetRate(ReadOnlySpan<short> input, int inputRate)
    {
        var outputLength = inputRate == TargetSampleRate
            ? input.Length
            : (int)Math.Ceiling(input.Length * (double)TargetSampleRate / inputRate);
        var output = new byte[outputLength * sizeof(short)];
        for (var index = 0; index < outputLength; index++)
        {
            var sourcePosition = index * (double)inputRate / TargetSampleRate;
            var first = Math.Min((int)sourcePosition, input.Length - 1);
            var second = Math.Min(first + 1, input.Length - 1);
            var fraction = sourcePosition - first;
            var sample = (short)Math.Clamp(
                (int)Math.Round(input[first] * (1 - fraction) + input[second] * fraction),
                short.MinValue,
                short.MaxValue);
            output[index * 2] = (byte)(sample & 0xff);
            output[index * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
        return output;
    }

    private async Task ReleaseCaptureCoreAsync()
    {
        var captureCts = _captureCts;
        var captureTask = _captureTask;
        var record = _record;
        _captureCts = null;
        _captureTask = null;
        _record = null;

        captureCts?.Cancel();
        if (record is not null)
        {
            if (_routingChangedListener is not null)
            {
                record.RemoveOnRoutingChangedListener(_routingChangedListener);
                _routingChangedListener.Dispose();
                _routingChangedListener = null;
            }
            try
            {
                if (record.RecordingState == RecordState.Recording)
                {
                    record.Stop();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            if (captureTask is not null)
            {
                try
                {
                    await captureTask.ConfigureAwait(false);
                }
                catch (System.OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            captureCts?.Dispose();
            DisposeEffect(_echoCanceler);
            DisposeEffect(_noiseSuppressor);
            DisposeEffect(_gainControl);
            _echoCanceler = null;
            _noiseSuppressor = null;
            _gainControl = null;
            if (record is not null)
            {
                record.Release();
                record.Dispose();
            }
        }
    }

    private static void DisposeEffect(AudioEffect? effect)
    {
        if (effect is null)
        {
            return;
        }
        effect.Release();
        effect.Dispose();
    }

    private void RestoreAudioMode()
    {
        if (_audioManager is not null)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26) && _audioFocusRequest is not null)
            {
                _audioManager.AbandonAudioFocusRequest(_audioFocusRequest);
            }
            else if (!OperatingSystem.IsAndroidVersionAtLeast(26) && _focusChangeListener is not null)
            {
                _audioManager.AbandonAudioFocus(_focusChangeListener);
            }
            _audioFocusRequest = null;
            _focusChangeListener?.Dispose();
            _focusChangeListener = null;
            _audioManager.Mode = _previousMode;
            _audioManager = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
    }

    private sealed class FocusChangeListener(Action<AudioFocus> onChanged)
        : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) => onChanged(focusChange);
    }

    private sealed class RoutingChangeListener(Action onChanged)
        : Java.Lang.Object, global::Android.Media.IAudioRoutingOnRoutingChangedListener
    {
        public void OnRoutingChanged(global::Android.Media.IAudioRouting? router) => onChanged();
    }
}
