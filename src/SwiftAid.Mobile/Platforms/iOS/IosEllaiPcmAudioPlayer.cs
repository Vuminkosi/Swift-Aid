using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using SwiftAid.UI.Ellai;
using UIKit;

namespace SwiftAid.Mobile.Platforms.iOS;

/// <summary>Streams transient Gateway PCM through AVAudioEngine. Buffer
/// scheduling waits for each bounded chunk to play before reading more data.</summary>
public sealed class IosEllaiPcmAudioPlayer(IosEllaiNativeVoiceCapture capture)
    : IEllaiPcmAudioPlayer, IDisposable
{
    private const int ReadBufferBytes = 8 * 1024;
    private const int MaximumPcmBytes = 3_840_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _engineGate = new();
    private AVAudioEngine? _activeEngine;
    private AVAudioPlayerNode? _activePlayer;
    private CancellationTokenSource? _activePlaybackCts;
    private NSObject? _interruptionObserver;
    private NSObject? _routeObserver;
    private NSObject? _backgroundObserver;
    private bool _disposed;

    public bool IsAvailable => !_disposed;

    public async Task PlayAsync(System.IO.Stream pcm, int sampleRate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (!pcm.CanRead || sampleRate is < 8_000 or > 48_000)
        {
            throw new ArgumentException("The Gateway PCM stream or sample rate is invalid.", nameof(pcm));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AVAudioEngine? engine = null;
        AVAudioPlayerNode? player = null;
        AVAudioFormat? format = null;
        AVAudioPcmBuffer? pendingBuffer = null;
        AudioSessionSnapshot? sessionSnapshot = null;
        CancellationTokenSource? playbackCts = null;
        byte[]? readBuffer = null;
        byte[]? pcmBuffer = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_engineGate)
            {
                _activePlaybackCts = playbackCts;
            }
            if (!capture.IsCapturing)
            {
                sessionSnapshot = ConfigurePlaybackAudioSession();
            }

            engine = new AVAudioEngine();
            player = new AVAudioPlayerNode();
            format = new AVAudioFormat(
                AVAudioCommonFormat.PCMInt16, sampleRate, 1, false);
            engine.AttachNode(player);
            engine.Connect(player, engine.MainMixerNode, format);
            engine.Prepare();
            if (!engine.StartAndReturnError(out var engineError))
            {
                throw new InvalidOperationException("iOS could not start speech playback: "
                    + GetErrorDescription(engineError));
            }
            GetErrorDescription(engineError);

            lock (_engineGate)
            {
                _activeEngine = engine;
                _activePlayer = player;
            }

            RegisterPlaybackNotifications();
            player.Play();
            readBuffer = new byte[ReadBufferBytes];
            pcmBuffer = new byte[ReadBufferBytes + 1];
            var hasPendingByte = false;
            byte pendingByte = 0;
            var totalBytes = 0;
            while (true)
            {
                playbackCts.Token.ThrowIfCancellationRequested();
                var read = await pcm.ReadAsync(readBuffer, playbackCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes = checked(totalBytes + read);
                if (totalBytes > MaximumPcmBytes)
                {
                    throw new InvalidOperationException("The Gateway PCM stream exceeds its playback limit.");
                }

                var writeCount = 0;
                var inputOffset = 0;
                if (hasPendingByte)
                {
                    pcmBuffer[writeCount++] = pendingByte;
                    pcmBuffer[writeCount++] = readBuffer[inputOffset++];
                    hasPendingByte = false;
                }

                var remainingReadBytes = read - inputOffset;
                var completeReadBytes = remainingReadBytes & ~1;
                Buffer.BlockCopy(readBuffer, inputOffset, pcmBuffer, writeCount, completeReadBytes);
                writeCount += completeReadBytes;
                if ((remainingReadBytes & 1) != 0)
                {
                    pendingByte = readBuffer[read - 1];
                    hasPendingByte = true;
                }

                if (writeCount == 0)
                {
                    continue;
                }

                var samples = new short[writeCount / 2];
                Buffer.BlockCopy(pcmBuffer, 0, samples, 0, writeCount);
                pendingBuffer = new AVAudioPcmBuffer(format, (uint)samples.Length)
                {
                    FrameLength = (uint)samples.Length,
                };
                var channelData = Marshal.ReadIntPtr(pendingBuffer.Int16ChannelData);
                if (channelData == IntPtr.Zero)
                {
                    throw new InvalidOperationException("iOS PCM playback memory is unavailable.");
                }
                Marshal.Copy(samples, 0, channelData, samples.Length);
                Array.Clear(samples);

                await player.ScheduleBufferAsync(
                    pendingBuffer, AVAudioPlayerNodeCompletionCallbackType.PlayedBack)
                    .WaitAsync(playbackCts.Token).ConfigureAwait(false);
                pendingBuffer.Dispose();
                pendingBuffer = null;
            }

            if (totalBytes == 0 || hasPendingByte)
            {
                throw new InvalidOperationException("The Gateway PCM stream is empty or incomplete.");
            }
        }
        finally
        {
            try
            {
                lock (_engineGate)
                {
                    if (ReferenceEquals(_activeEngine, engine))
                    {
                        _activeEngine = null;
                        _activePlayer = null;
                    }
                    if (ReferenceEquals(_activePlaybackCts, playbackCts))
                    {
                        _activePlaybackCts = null;
                    }
                }

                try
                {
                    RemovePlaybackNotifications();
                }
                catch (Exception)
                {
                }
                try
                {
                    player?.Stop();
                }
                catch (Exception)
                {
                }
                try
                {
                    engine?.Stop();
                }
                catch (Exception)
                {
                }
                pendingBuffer?.Dispose();
                player?.Dispose();
                engine?.Dispose();
                format?.Dispose();
            }
            finally
            {
                if (readBuffer is not null)
                {
                    Array.Clear(readBuffer);
                }
                if (pcmBuffer is not null)
                {
                    Array.Clear(pcmBuffer);
                }
                playbackCts?.Dispose();
                if (sessionSnapshot is not null && !capture.IsCapturing)
                {
                    RestoreAudioSession(sessionSnapshot);
                }
                _gate.Release();
            }
        }
    }

    public Task StopAsync()
    {
        CancellationTokenSource? playbackCts;
        AVAudioPlayerNode? player;
        AVAudioEngine? engine;
        lock (_engineGate)
        {
            playbackCts = _activePlaybackCts;
            player = _activePlayer;
            engine = _activeEngine;
        }

        try
        {
            playbackCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            player?.Stop();
        }
        catch (Exception)
        {
        }
        try
        {
            engine?.Stop();
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
    }

    private void RegisterPlaybackNotifications()
    {
        _interruptionObserver = AVAudioSession.Notifications.ObserveInterruption(
            (_, _) => StopForSystemEvent());
        var center = NSNotificationCenter.DefaultCenter;
        _routeObserver = center.AddObserver(
            AVAudioSession.RouteChangeNotification, _ => StopForSystemEvent());
        _backgroundObserver = center.AddObserver(
            UIApplication.DidEnterBackgroundNotification, _ => StopForSystemEvent());
    }

    private void StopForSystemEvent() => _ = StopAsync();

    private void RemovePlaybackNotifications()
    {
        RemoveObserver(ref _interruptionObserver);
        RemoveObserver(ref _routeObserver);
        RemoveObserver(ref _backgroundObserver);
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

    private static AudioSessionSnapshot ConfigurePlaybackAudioSession()
    {
        var session = AVAudioSession.SharedInstance();
        var snapshot = new AudioSessionSnapshot(
            session.Category?.ToString() ?? "AVAudioSessionCategorySoloAmbient",
            session.Mode?.ToString() ?? "AVAudioSessionModeDefault",
            session.CategoryOptions);
        var options = AVAudioSessionCategoryOptions.AllowBluetooth
            | AVAudioSessionCategoryOptions.DefaultToSpeaker;
        if (!session.SetCategory(
                AVAudioSessionCategory.PlayAndRecord,
                AVAudioSessionMode.VoiceChat,
                options,
                out var categoryError))
        {
            RestoreAudioSession(snapshot);
            throw new InvalidOperationException("iOS could not configure speech playback: "
                + GetErrorDescription(categoryError));
        }
        GetErrorDescription(categoryError);

        if (!session.SetActive(true, out var activationError))
        {
            RestoreAudioSession(snapshot);
            throw new InvalidOperationException("iOS could not activate speech playback: "
                + GetErrorDescription(activationError));
        }
        GetErrorDescription(activationError);
        return snapshot;
    }

    private static void RestoreAudioSession(AudioSessionSnapshot snapshot)
    {
        var session = AVAudioSession.SharedInstance();
        try
        {
            session.SetCategory(
                snapshot.Category,
                snapshot.Mode,
                snapshot.Options,
                out var restoreError);
            GetErrorDescription(restoreError);
        }
        catch (Exception)
        {
            // Keep playback cleanup intact if iOS rejects restoring the prior route.
        }
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }

    private sealed record AudioSessionSnapshot(
        string Category,
        string Mode,
        AVAudioSessionCategoryOptions Options);
}
