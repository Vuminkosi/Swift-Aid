using System.Buffers.Binary;

namespace SwiftAid.Client.Intake;

/// <summary>Converts bounded decoded PCM16 audio into the mono 24 kHz WAV used by voice transcription.
/// The caller must clear the returned audio buffer after use.</summary>
public static class ReportVoicePcmWaveEncoder
{
    public const int OutputSampleRate = 24000;
    private const int MaximumDurationSeconds = 125;

    public static byte[] Encode(ReadOnlySpan<byte> pcm, int sampleRate, int channels)
    {
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "The decoded sample rate is unsupported.");
        if (channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(channels), "The decoded channel count is unsupported.");
        var bytesPerFrame = checked(channels * sizeof(short));
        if (pcm.IsEmpty || pcm.Length % bytesPerFrame != 0)
            throw new ArgumentException("The decoded audio does not contain complete PCM frames.", nameof(pcm));
        var frames = pcm.Length / bytesPerFrame;
        if (frames > (long)sampleRate * MaximumDurationSeconds)
            throw new ArgumentException("The recording exceeds the supported duration.", nameof(pcm));

        var outputFrames = checked((int)(((long)frames * OutputSampleRate + sampleRate - 1) / sampleRate));
        var wave = new byte[checked(44 + outputFrames * sizeof(short))];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8, 8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), OutputSampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), OutputSampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), wave.Length - 44);

        for (var index = 0; index < outputFrames; index++)
        {
            var numerator = (long)index * sampleRate;
            var first = (int)Math.Min(numerator / OutputSampleRate, frames - 1);
            var second = Math.Min(first + 1, frames - 1);
            var fraction = (int)(numerator % OutputSampleRate);
            var left = ReadMono(pcm, first, channels);
            var right = ReadMono(pcm, second, channels);
            var sample = (left * (OutputSampleRate - fraction) + right * fraction) / OutputSampleRate;
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + index * 2, 2), (short)sample);
        }

        return wave;
    }

    private static int ReadMono(ReadOnlySpan<byte> pcm, int frame, int channels)
    {
        var offset = frame * channels * sizeof(short);
        var first = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset, 2));
        return channels == 1 ? first :
            (first + BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset + 2, 2))) / 2;
    }
}
