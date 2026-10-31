using Android.Media;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Mobile.Platforms.Android;

/// <summary>Native Android AudioTrack playback for Gateway-generated Ellai PCM.</summary>
public sealed class AndroidEllaiPcmAudioPlayer : IEllaiPcmAudioPlayer, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _trackGate = new();
    private AudioTrack? _activeTrack;
    private bool _disposed;

    public bool IsAvailable => !_disposed;

    public async Task PlayAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length is 0 or > 3_840_000 || (pcm.Length & 1) != 0
            || sampleRate is < 8_000 or > 48_000)
        {
            throw new ArgumentOutOfRangeException(nameof(pcm), "The Gateway PCM stream is invalid or exceeds its limit.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AudioTrack? track = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var attributesBuilder = new AudioAttributes.Builder()!;
            attributesBuilder.SetUsage(AudioUsageKind.VoiceCommunication);
            attributesBuilder.SetContentType(AudioContentType.Speech);
            var attributes = attributesBuilder.Build()
                ?? throw new InvalidOperationException("Android audio attributes are unavailable.");
            var formatBuilder = new AudioFormat.Builder()!;
            formatBuilder.SetEncoding(Encoding.Pcm16bit);
            formatBuilder.SetSampleRate(sampleRate);
            formatBuilder.SetChannelMask(ChannelOut.Mono);
            var format = formatBuilder.Build()
                ?? throw new InvalidOperationException("Android PCM format is unavailable.");
            var minimumBufferSize = AudioTrack.GetMinBufferSize(
                sampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
            if (minimumBufferSize <= 0)
            {
                throw new InvalidOperationException("Android cannot play the Gateway PCM format.");
            }

            track = new AudioTrack.Builder()
                .SetAudioAttributes(attributes)
                .SetAudioFormat(format)
                .SetTransferMode(AudioTrackMode.Stream)
                .SetBufferSizeInBytes(Math.Max(minimumBufferSize, sampleRate * 2))
                .Build();
            lock (_trackGate)
            {
                _activeTrack = track;
            }

            track.Play();
            const int writeSize = 32 * 1024;
            for (var offset = 0; offset < pcm.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(writeSize, pcm.Length - offset);
                var written = await Task.Run(
                    () => track.Write(pcm, offset, count, WriteMode.Blocking), cancellationToken)
                    .ConfigureAwait(false);
                if (written <= 0)
                {
                    throw new InvalidOperationException("Android stopped accepting Ellai PCM.");
                }

                offset += written;
            }

            var totalFrames = pcm.Length / 2;
            while (track.PlaybackHeadPosition < totalFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_trackGate)
            {
                if (ReferenceEquals(_activeTrack, track))
                {
                    _activeTrack = null;
                }
            }

            if (track is not null)
            {
                try
                {
                    track.Stop();
                }
                catch (InvalidOperationException)
                {
                }
                track.Release();
                track.Dispose();
            }

            _gate.Release();
        }
    }

    public Task StopAsync()
    {
        lock (_trackGate)
        {
            if (_activeTrack is { } track)
            {
                try
                {
                    track.Stop();
                    track.Flush();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _gate.Dispose();
    }
}
