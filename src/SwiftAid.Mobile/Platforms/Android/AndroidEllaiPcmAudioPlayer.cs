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

    public async Task PlayAsync(System.IO.Stream pcm, int sampleRate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (!pcm.CanRead || sampleRate is < 8_000 or > 48_000)
        {
            throw new ArgumentException("The Gateway PCM stream or sample rate is invalid.", nameof(pcm));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AudioTrack? track = null;
        byte[]? readBuffer = null;
        byte[]? writeBuffer = null;
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
                .SetBufferSizeInBytes(Math.Max(minimumBufferSize, sampleRate / 2))
                .Build();
            lock (_trackGate)
            {
                _activeTrack = track;
            }

            track.Play();
            const int readSize = 8 * 1024;
            const int maximumPcmBytes = 3_840_000;
            readBuffer = new byte[readSize];
            writeBuffer = new byte[readSize + 1];
            var hasPendingByte = false;
            byte pendingByte = 0;
            var totalBytes = 0;
            var totalFrames = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await pcm.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes = checked(totalBytes + read);
                if (totalBytes > maximumPcmBytes)
                {
                    throw new InvalidOperationException("The Gateway PCM stream exceeds its playback limit.");
                }

                var writeCount = 0;
                var inputOffset = 0;
                if (hasPendingByte)
                {
                    writeBuffer[writeCount++] = pendingByte;
                    writeBuffer[writeCount++] = readBuffer[inputOffset++];
                    hasPendingByte = false;
                }

                var remainingReadBytes = read - inputOffset;
                var completeReadBytes = remainingReadBytes & ~1;
                Buffer.BlockCopy(readBuffer, inputOffset, writeBuffer, writeCount, completeReadBytes);
                writeCount += completeReadBytes;
                if ((remainingReadBytes & 1) != 0)
                {
                    pendingByte = readBuffer[read - 1];
                    hasPendingByte = true;
                }

                var offset = 0;
                while (offset < writeCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var written = await Task.Run(
                        () => track.Write(writeBuffer, offset, writeCount - offset, WriteMode.Blocking),
                        cancellationToken).ConfigureAwait(false);
                    if (written <= 0 || (written & 1) != 0)
                    {
                        throw new InvalidOperationException("Android stopped accepting Ellai PCM.");
                    }

                    offset += written;
                    totalFrames += written / 2;
                }
            }

            if (totalBytes == 0 || hasPendingByte)
            {
                throw new InvalidOperationException("The Gateway PCM stream is empty or incomplete.");
            }

            while (track.PlaybackHeadPosition < totalFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
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
                    try
                    {
                        track.Release();
                    }
                    finally
                    {
                        track.Dispose();
                    }
                }
            }
            finally
            {
                if (readBuffer is not null)
                {
                    Array.Clear(readBuffer);
                }
                if (writeBuffer is not null)
                {
                    Array.Clear(writeBuffer);
                }
                _gate.Release();
            }
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
    }
}
