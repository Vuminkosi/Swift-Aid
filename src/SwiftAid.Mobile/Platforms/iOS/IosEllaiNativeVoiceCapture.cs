using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AVFoundation;
using Foundation;
using SwiftAid.UI.Ellai;
using UIKit;

namespace SwiftAid.Mobile.Platforms.iOS;

/// <summary>Transient Ellai capture through iOS voice chat processing. The
/// WebView path remains available for recorded capture and live fallback.</summary>
public sealed class IosEllaiNativeVoiceCapture : IEllaiNativeVoiceCapture, IAsyncDisposable
{
    private const int MaximumInputFrames = 8_192;
    private const int MaximumQueuedChunks = 8;
    private const int PcmSampleRate = 24_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AVAudioEngine? _engine;
    private AVAudioInputNode? _input;
    private AVAudioSession? _session;
    private Channel<byte[]>? _audioFrames;
    private CancellationTokenSource? _captureCts;
    private CancellationTokenRegistration _cancellationRegistration;
    private Task? _consumerTask;
    private NSObject? _interruptionObserver;
    private NSObject? _routeObserver;
    private NSObject? _backgroundObserver;
    private string? _previousCategory;
    private string? _previousMode;
    private AVAudioSessionCategoryOptions _previousCategoryOptions;
    private bool _sessionConfigured;
    private bool _voiceProcessingEnabled;
    private bool _disposed;
    private int _stopRequested;

    public bool IsAvailable => !_disposed;
    public bool IsEchoCancellationEnabled => _engine is not null && _voiceProcessingEnabled;

    public bool IsCapturing => _engine is not null;

    public event Action<string>? Interrupted;
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
            if (_engine is not null)
            {
                throw new InvalidOperationException("Ellai native microphone capture is already active.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            ConfigureAudioSession();

            AVAudioEngine? engine = null;
            try
            {
                engine = new AVAudioEngine();
                var input = engine.InputNode
                    ?? throw new InvalidOperationException("iOS audio input is unavailable.");
                var inputFormat = input.GetBusOutputFormat(0);
                if (inputFormat.SampleRate <= 0 || inputFormat.ChannelCount is 0 or > 8
                    || inputFormat.CommonFormat != AVAudioCommonFormat.PCMFloat32
                    || inputFormat.Interleaved)
                {
                    throw new InvalidOperationException(
                        "iOS returned an unsupported microphone format. Try again or type your message.");
                }

                var voiceProcessingEnabled = input.SetVoiceProcessingEnabled(true, out var voiceProcessingError);
                var voiceProcessingErrorText = GetErrorDescription(voiceProcessingError);
                var captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaximumQueuedChunks)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                });

                input.InstallTapOnBus(0, 960, inputFormat, (buffer, _) =>
                    OnAudioBuffer(buffer, inputFormat, channel.Writer, captureCts));

                _engine = engine;
                _input = input;
                _voiceProcessingEnabled = voiceProcessingEnabled;
                _captureCts = captureCts;
                _audioFrames = channel;
                _consumerTask = ConsumeFramesAsync(channel.Reader, onPcmChunk, captureCts.Token);
                _cancellationRegistration = cancellationToken.Register(RequestStop);
                RegisterAudioNotifications();

                engine.Prepare();
                if (!engine.StartAndReturnError(out var startError))
                {
                    var startErrorText = GetErrorDescription(startError) ?? voiceProcessingErrorText;
                    throw new InvalidOperationException(
                        "iOS could not start voice capture. "
                        + startErrorText);
                }
                GetErrorDescription(startError);

                return voiceProcessingEnabled
                    ? "iOS voice chat capture active; system voice processing enabled."
                    : "iOS voice chat capture active; system voice processing is unavailable on this route."
                        + (voiceProcessingError is null ? string.Empty : " Use a headset or type your message.");
            }
            catch
            {
                if (_engine is null && engine is not null)
                {
                    InputTapCleanup(engine);
                }
                await ReleaseCaptureCoreAsync().ConfigureAwait(false);
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
            _gate.Release();
        }
    }

    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        Interrupted?.Invoke(
            "Voice capture stopped when the app moved to the background. Try again or type your message.");
    }

    private void ConfigureAudioSession()
    {
        var session = AVAudioSession.SharedInstance();
        _session = session;
        _previousCategory = session.Category?.ToString();
        _previousMode = session.Mode?.ToString();
        _previousCategoryOptions = session.CategoryOptions;
        _sessionConfigured = true;

        var options = AVAudioSessionCategoryOptions.AllowBluetooth
            | AVAudioSessionCategoryOptions.DefaultToSpeaker;
        var categoryConfigured = session.SetCategory(
                AVAudioSessionCategory.PlayAndRecord,
                AVAudioSessionMode.VoiceChat,
                options,
                out var categoryError);
        var categoryErrorText = GetErrorDescription(categoryError);
        if (!categoryConfigured)
        {
            RestoreAudioSession();
            throw new InvalidOperationException(
                "iOS could not configure voice chat audio. " + categoryErrorText);
        }

        var activated = session.SetActive(true, out var activationError);
        var activationErrorText = GetErrorDescription(activationError);
        if (!activated)
        {
            RestoreAudioSession();
            throw new InvalidOperationException(
                "iOS could not activate voice capture. " + activationErrorText);
        }
    }

    private void OnAudioBuffer(
        AVAudioPcmBuffer buffer,
        AVAudioFormat format,
        ChannelWriter<byte[]> writer,
        CancellationTokenSource captureCts)
    {
        if (captureCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var frameCount = checked((int)buffer.FrameLength);
            if (frameCount == 0)
            {
                return;
            }
            if (frameCount > MaximumInputFrames || buffer.FloatChannelData == IntPtr.Zero)
            {
                InterruptAndStop(
                    "iOS microphone returned an unsupported audio buffer. Try again or type your message.");
                return;
            }

            var channelCount = (int)format.ChannelCount;
            var channels = ArrayPool<float>.Shared.Rent(frameCount * channelCount);
            var channelSamples = ArrayPool<float>.Shared.Rent(frameCount);
            try
            {
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var samples = Marshal.ReadIntPtr(
                        buffer.FloatChannelData, channel * IntPtr.Size);
                    if (samples == IntPtr.Zero)
                    {
                        InterruptAndStop(
                            "iOS microphone data was unavailable. Try again or type your message.");
                        return;
                    }

                    Marshal.Copy(samples, channelSamples, 0, frameCount);
                    for (var frame = 0; frame < frameCount; frame++)
                    {
                        channels[frame * channelCount + channel] = channelSamples[frame];
                    }
                }

                var pcm = EllaiVoicePcm16Converter.ConvertToMonoPcm16(
                    channels.AsSpan(0, frameCount * channelCount), channelCount, format.SampleRate);
                if (pcm.Length == 0)
                {
                    return;
                }

                if (!writer.TryWrite(pcm))
                {
                    Array.Clear(pcm);
                    InterruptAndStop(
                        "Voice audio could not be sent quickly enough. The turn was discarded; try again or type your message.");
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(channels, clearArray: true);
                ArrayPool<float>.Shared.Return(channelSamples, clearArray: true);
            }
        }
        catch (Exception)
        {
            InterruptAndStop(
                "iOS voice capture stopped unexpectedly. Try again or type your message.");
        }
    }

    private async Task ConsumeFramesAsync(
        ChannelReader<byte[]> reader,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onPcmChunk,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var pcm in reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    await onPcmChunk(pcm, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception)
                {
                    InterruptAndStop(
                        "Voice audio could not be sent. The turn was discarded; try again or type your message.");
                }
                finally
                {
                    Array.Clear(pcm);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RegisterAudioNotifications()
    {
        var center = NSNotificationCenter.DefaultCenter;
        var session = _session ?? throw new InvalidOperationException("iOS audio session is unavailable.");
        _interruptionObserver = AVAudioSession.Notifications.ObserveInterruption(
            (_, _) => InterruptAndStop(
                "An audio interruption stopped voice capture. Try again or type your message."));
        _routeObserver = center.AddObserver(
            AVAudioSession.RouteChangeNotification, HandleRouteChanged, session);
        _backgroundObserver = center.AddObserver(
            UIApplication.DidEnterBackgroundNotification,
            _ => InterruptAndStop(
                "Voice capture stopped when the app moved to the background. Try again or type your message."));
    }

    private void HandleRouteChanged(NSNotification notification)
    {
        try
        {
            RouteChanged?.Invoke();
        }
        catch (Exception)
        {
            // A UI subscriber must not escape into the native notification callback.
        }
        if (_session?.CurrentRoute.Inputs.Length == 0)
        {
            InterruptAndStop(
                "The microphone route disconnected. Voice capture stopped; try again or type your message.");
        }
    }

    private void InterruptAndStop(string message)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        try
        {
            Interrupted?.Invoke(message);
        }
        catch (Exception)
        {
            // Continue releasing the microphone even if a UI subscriber fails.
        }
        try
        {
            _captureCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private void RequestStop()
    {
        _captureCts?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private async Task ReleaseCaptureCoreAsync()
    {
        _cancellationRegistration.Dispose();
        _cancellationRegistration = default;
        RemoveObserver(ref _interruptionObserver);
        RemoveObserver(ref _routeObserver);
        RemoveObserver(ref _backgroundObserver);

        var captureCts = _captureCts;
        var consumerTask = _consumerTask;
        var channel = _audioFrames;
        var input = _input;
        var engine = _engine;
        _captureCts = null;
        _consumerTask = null;
        _audioFrames = null;
        _input = null;
        _engine = null;
        _voiceProcessingEnabled = false;

        captureCts?.Cancel();
        if (input is not null)
        {
            try
            {
                input.RemoveTapOnBus(0);
            }
            catch (Exception)
            {
                // Continue releasing the engine and audio session.
            }
        }
        try
        {
            engine?.Stop();
        }
        catch (Exception)
        {
            // The engine can already be stopped after a route or interruption event.
        }
        channel?.Writer.TryComplete();

        try
        {
            if (consumerTask is not null)
            {
                await consumerTask.ConfigureAwait(false);
            }
        }
        finally
        {
            if (channel is not null)
            {
                while (channel.Reader.TryRead(out var remaining))
                {
                    Array.Clear(remaining);
                }
            }
            captureCts?.Dispose();
            input?.Dispose();
            engine?.Dispose();
            RestoreAudioSession();
            Interlocked.Exchange(ref _stopRequested, 0);
        }
    }

    private void RestoreAudioSession()
    {
        var session = _session;
        if (session is null || !_sessionConfigured)
        {
            return;
        }

        _sessionConfigured = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(_previousCategory)
                && !string.IsNullOrWhiteSpace(_previousMode))
            {
                session.SetCategory(
                    _previousCategory,
                    _previousMode,
                    _previousCategoryOptions,
                    out var restoreError);
                GetErrorDescription(restoreError);
            }
        }
        catch (Exception)
        {
            // Preserve capture cleanup if iOS rejects restoration after a route change.
        }
        finally
        {
            _session = null;
            _previousCategory = null;
            _previousMode = null;
            _previousCategoryOptions = default;
        }
    }

    private static void RemoveObserver(ref NSObject? observer)
    {
        if (observer is null)
        {
            return;
        }
        NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        observer.Dispose();
        observer = null;
    }

    private static string? GetErrorDescription(NSError? error)
    {
        if (error is null)
        {
            return null;
        }
        var description = error.LocalizedDescription;
        error.Dispose();
        return description;
    }

    private static void InputTapCleanup(AVAudioEngine engine)
    {
        try
        {
            engine.InputNode?.RemoveTapOnBus(0);
            engine.Stop();
        }
        catch (Exception)
        {
        }
        engine.Dispose();
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
}
