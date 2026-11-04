namespace SwiftAid.UI.Ellai;

/// <summary>Converts interleaved floating-point microphone frames to bounded
/// mono little-endian PCM16 at the Ellai live-capture rate.</summary>
public static class EllaiVoicePcm16Converter
{
    public const int TargetSampleRate = 24_000;
    private const int MaximumOutputSamples = 4_096;

    public static byte[] ConvertToMonoPcm16(
        ReadOnlySpan<float> interleavedSamples,
        int channelCount,
        double inputSampleRate)
    {
        if (channelCount is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }
        if (interleavedSamples.IsEmpty || interleavedSamples.Length % channelCount != 0)
        {
            throw new ArgumentException("Audio frames must contain complete interleaved samples.",
                nameof(interleavedSamples));
        }
        if (!double.IsFinite(inputSampleRate) || inputSampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSampleRate));
        }

        var inputFrameCount = interleavedSamples.Length / channelCount;
        var outputFrameCount = checked((int)Math.Ceiling(
            inputFrameCount * TargetSampleRate / inputSampleRate));
        if (outputFrameCount > MaximumOutputSamples)
        {
            throw new ArgumentOutOfRangeException(nameof(interleavedSamples),
                "The audio frame exceeds the bounded PCM output size.");
        }

        var output = new byte[checked(outputFrameCount * sizeof(short))];
        for (var outputFrame = 0; outputFrame < outputFrameCount; outputFrame++)
        {
            var sourcePosition = outputFrame * inputSampleRate / TargetSampleRate;
            var firstFrame = Math.Min((int)sourcePosition, inputFrameCount - 1);
            var secondFrame = Math.Min(firstFrame + 1, inputFrameCount - 1);
            var fraction = sourcePosition - firstFrame;
            double first = 0;
            double second = 0;
            for (var channel = 0; channel < channelCount; channel++)
            {
                first += interleavedSamples[firstFrame * channelCount + channel];
                second += interleavedSamples[secondFrame * channelCount + channel];
            }

            var mono = (first * (1 - fraction) + second * fraction) / channelCount;
            var sample = (short)Math.Clamp(
                (int)Math.Round(mono * short.MaxValue), short.MinValue, short.MaxValue);
            output[outputFrame * 2] = (byte)(sample & 0xff);
            output[outputFrame * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
        return output;
    }
}
