using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
public sealed class EllaiClientBargeInDetectorTests
{
    [Fact]
    public void SilenceAndSteadyToneDoNotConfirmBargeIn()
    {
        using var detector = new EllaiClientBargeInDetector();
        var silence = new byte[960 * sizeof(short)];
        var transientNoise = CreateTransientNoise(durationMilliseconds: 400);

        for (var offset = 0; offset < silence.Length; offset += 960 * sizeof(short))
        {
            Assert.False(detector.ObservePcm24Khz(silence.AsSpan(offset, 960 * sizeof(short))));
        }
        for (var offset = 0; offset < transientNoise.Length; offset += 960 * sizeof(short))
        {
            Assert.False(detector.ObservePcm24Khz(transientNoise.AsSpan(offset, 960 * sizeof(short))));
        }
    }

    [Fact]
    public void VoicedInputConfirmsOnlyAfterTheConfiguredSpeechDuration()
    {
        using var detector = new EllaiClientBargeInDetector();
        var voiced = CreateVoicedSignal(durationMilliseconds: 400);
        var confirmed = false;
        var confirmations = 0;

        for (var offset = 0; offset < voiced.Length; offset += 960 * sizeof(short))
        {
            if (detector.ObservePcm24Khz(voiced.AsSpan(offset, 960 * sizeof(short))))
            {
                confirmed = true;
                confirmations++;
            }
        }

        Assert.True(confirmed);
        Assert.Equal(1, confirmations);
        Assert.Equal(180, EllaiClientBargeInDetector.ConfirmationMilliseconds);
    }

    [Fact]
    public void ResetAllowsAnotherBargeInAfterConfirmedSpeechWithoutRestartingCapture()
    {
        using var detector = new EllaiClientBargeInDetector();
        var voiced = CreateVoicedSignal(durationMilliseconds: 400);
        var silence = new byte[960 * sizeof(short) * 10];
        const int chunkBytes = 960 * sizeof(short);
        var firstTurnConfirmed = false;
        for (var offset = 0; offset < voiced.Length; offset += chunkBytes)
        {
            firstTurnConfirmed |= detector.ObservePcm24Khz(voiced.AsSpan(offset, chunkBytes));
        }
        Assert.True(firstTurnConfirmed);

        detector.ResetSpeechConfirmation();

        for (var offset = 0; offset < silence.Length; offset += chunkBytes)
        {
            Assert.False(detector.ObservePcm24Khz(silence.AsSpan(offset, chunkBytes)));
        }

        var secondTurnConfirmed = false;
        for (var offset = 0; offset < voiced.Length; offset += chunkBytes)
        {
            secondTurnConfirmed |= detector.ObservePcm24Khz(voiced.AsSpan(offset, chunkBytes));
        }
        Assert.True(secondTurnConfirmed);
    }

    private static byte[] CreateTransientNoise(int durationMilliseconds)
    {
        var sampleCount = 24_000 * durationMilliseconds / 1_000;
        var pcm = new byte[sampleCount * sizeof(short)];
        var random = new Random(41);
        for (var index = 0; index < sampleCount; index++)
        {
            var inTransient = index % 2_400 < 120;
            var value = inTransient ? (short)random.Next(short.MinValue, short.MaxValue) : (short)0;
            WriteSample(pcm, index, value);
        }

        return pcm;
    }

    private static byte[] CreateVoicedSignal(int durationMilliseconds)
    {
        var sampleCount = 24_000 * durationMilliseconds / 1_000;
        var pcm = new byte[sampleCount * sizeof(short)];
        var random = new Random(17);
        double phase = 0;
        for (var index = 0; index < sampleCount; index++)
        {
            var time = (double)index / 24_000;
            var fundamental = 118 + 18 * Math.Sin(2 * Math.PI * 2.1 * time);
            phase += 2 * Math.PI * fundamental / 24_000;
            var syllable = 0.35 + 0.65 * Math.Max(0, Math.Sin(2 * Math.PI * 3.4 * time));
            var voiced = 0.30 * Math.Sin(phase)
                + 0.24 * Math.Sin(phase * 2)
                + 0.18 * Math.Sin(phase * 3)
                + 0.13 * Math.Sin(phase * 4)
                + 0.08 * Math.Sin(phase * 5);
            var aspiration = (random.NextDouble() - 0.5) * 0.035;
            var value = (short)Math.Clamp((int)((voiced * syllable + aspiration) * 18_000),
                short.MinValue, short.MaxValue);
            WriteSample(pcm, index, value);
        }

        return pcm;
    }

    private static void WriteSample(byte[] pcm, int index, short sample)
    {
        pcm[index * 2] = (byte)(sample & 0xff);
        pcm[index * 2 + 1] = (byte)(sample >> 8);
    }
}
