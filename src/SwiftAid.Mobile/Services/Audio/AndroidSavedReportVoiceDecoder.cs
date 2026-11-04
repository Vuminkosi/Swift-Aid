#if ANDROID
using System.Diagnostics;
using System.Security.Cryptography;
using Android.Media;
using SwiftAid.Client.Intake;

namespace SwiftAid.Mobile.Services.Audio;

/// <summary>Decodes an original AAC note in memory for transcription without changing the saved original.</summary>
public static class AndroidSavedReportVoiceDecoder
{
    private const int MaximumEncodedBytes = 10 * 1024 * 1024;
    private const int MaximumDecodedBytes = 64 * 1024 * 1024;

    public static Task<byte[]> DecodeAsync(byte[] original, CancellationToken cancellationToken) =>
        Task.Run(() => Decode(original, cancellationToken), cancellationToken);

    private static byte[] Decode(byte[] original, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Length is 0 or > MaximumEncodedBytes)
            throw new ArgumentException("The recording exceeds the supported size.", nameof(original));

        using var source = new InMemoryMediaSource(original);
        using var extractor = new MediaExtractor();
        var chunks = new List<byte[]>();
        var decodedLength = 0;
        MediaCodec? codec = null;
        try
        {
            extractor.SetDataSource(source);
            MediaFormat? selected = null;
            for (var index = 0; index < extractor.TrackCount; index++)
            {
                var candidate = extractor.GetTrackFormat(index);
                var mime = candidate?.GetString(MediaFormat.KeyMime);
                if (string.Equals(mime, "audio/mp4a-latm", StringComparison.OrdinalIgnoreCase))
                {
                    selected = candidate;
                    extractor.SelectTrack(index);
                    break;
                }
                candidate?.Dispose();
            }
            using (selected)
            {
                if (selected is null) throw new InvalidDataException("The saved recording format is unavailable.");
                codec = MediaCodec.CreateDecoderByType("audio/mp4a-latm")
                    ?? throw new InvalidOperationException("An audio decoder is unavailable.");
                codec.Configure(selected, null, null, MediaCodecConfigFlags.None);
                codec.Start();
                using var info = new MediaCodec.BufferInfo();
                var inputFinished = false;
                var outputFinished = false;
                var sampleRate = 0;
                var channels = 0;
                var elapsed = Stopwatch.StartNew();
                while (!outputFinished)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Audio decoding timed out.");
                    if (!inputFinished)
                    {
                        var index = codec.DequeueInputBuffer(10_000);
                        if (index >= 0)
                        {
                            using var buffer = codec.GetInputBuffer(index)
                                ?? throw new InvalidDataException("The audio decoder input is unavailable.");
                            buffer.Clear();
                            var count = extractor.ReadSampleData(buffer, 0);
                            if (count < 0)
                            {
                                codec.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                                inputFinished = true;
                            }
                            else
                            {
                                codec.QueueInputBuffer(index, 0, count, Math.Max(0, extractor.SampleTime), MediaCodecBufferFlags.None);
                                extractor.Advance();
                            }
                        }
                    }

                    var outputIndex = codec.DequeueOutputBuffer(info, 10_000);
                    if (outputIndex == (int)MediaCodecInfoState.OutputFormatChanged)
                    {
                        using var format = codec.OutputFormat;
                        var nextSampleRate = format.GetInteger(MediaFormat.KeySampleRate);
                        var nextChannels = format.GetInteger(MediaFormat.KeyChannelCount);
                        if (sampleRate != 0 && (sampleRate != nextSampleRate || channels != nextChannels))
                            throw new InvalidDataException("The decoded audio format changed during the recording.");
                        sampleRate = nextSampleRate;
                        channels = nextChannels;
                        if (format.ContainsKey(MediaFormat.KeyPcmEncoding) &&
                            format.GetInteger(MediaFormat.KeyPcmEncoding) != (int)Encoding.Pcm16bit)
                            throw new InvalidDataException("The decoded audio encoding is unsupported.");
                    }
                    else if (outputIndex >= 0)
                    {
                        try
                        {
                            if (info.Size > 0)
                            {
                                if (info.Size > MaximumDecodedBytes - decodedLength)
                                    throw new InvalidDataException("The decoded recording exceeds the supported size.");
                                using var buffer = codec.GetOutputBuffer(outputIndex)
                                    ?? throw new InvalidDataException("The audio decoder output is unavailable.");
                                buffer.Position(info.Offset);
                                var chunk = new byte[info.Size];
                                try { buffer.Get(chunk); chunks.Add(chunk); decodedLength += chunk.Length; }
                                catch { CryptographicOperations.ZeroMemory(chunk); throw; }
                            }
                            outputFinished = (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                        }
                        finally { codec.ReleaseOutputBuffer(outputIndex, false); }
                    }
                }
                var decoded = new byte[decodedLength];
                try
                {
                    var offset = 0;
                    foreach (var chunk in chunks) { chunk.CopyTo(decoded, offset); offset += chunk.Length; }
                    return ReportVoicePcmWaveEncoder.Encode(decoded, sampleRate, channels);
                }
                finally { CryptographicOperations.ZeroMemory(decoded); }
            }
        }
        finally
        {
            foreach (var chunk in chunks) CryptographicOperations.ZeroMemory(chunk);
            if (codec is not null)
            {
                try { codec.Stop(); } catch (Java.Lang.IllegalStateException) { }
                codec.Release();
                codec.Dispose();
            }
            extractor.Release();
        }
    }

    private sealed class InMemoryMediaSource(byte[] audio) : MediaDataSource
    {
        public override long Size => audio.LongLength;
        public override int ReadAt(long position, byte[]? buffer, int offset, int size)
        {
            if (size == 0) return 0;
            if (buffer is null || position < 0 || position >= audio.LongLength) return -1;
            var count = (int)Math.Min(size, audio.LongLength - position);
            Array.Copy(audio, position, buffer, offset, count);
            return count;
        }
        public override void Close() { }
    }
}
#endif
