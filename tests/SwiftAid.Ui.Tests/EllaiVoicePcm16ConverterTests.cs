using System.Buffers.Binary;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
public sealed class EllaiVoicePcm16ConverterTests
{
    [Fact]
    public void ConvertsTargetRateMonoSamplesToLittleEndianPcm16()
    {
        float[] input = [-1f, 0f, 0.5f, 1f];

        var output = EllaiVoicePcm16Converter.ConvertToMonoPcm16(input, 1, 24_000);

        Assert.Equal(8, output.Length);
        Assert.Equal([-32767, 0, 16384, 32767], DecodeSamples(output));
    }

    [Fact]
    public void ResamplesFortyEightKilohertzCaptureToTwentyFourKilohertz()
    {
        var input = Enumerable.Repeat(0.5f, 960).ToArray();

        var output = EllaiVoicePcm16Converter.ConvertToMonoPcm16(input, 1, 48_000);

        Assert.Equal(480 * sizeof(short), output.Length);
        Assert.All(DecodeSamples(output), sample => Assert.Equal(16384, sample));
    }

    [Fact]
    public void DownmixesStereoFramesBeforeEncoding()
    {
        var input = new float[480];
        for (var frame = 0; frame < input.Length / 2; frame++)
        {
            input[frame * 2] = 0.75f;
            input[(frame * 2) + 1] = -0.75f;
        }

        var output = EllaiVoicePcm16Converter.ConvertToMonoPcm16(input, 2, 24_000);

        Assert.Equal(240 * sizeof(short), output.Length);
        Assert.All(DecodeSamples(output), sample => Assert.Equal(0, sample));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7_999)]
    [InlineData(192_001)]
    public void RejectsUnsupportedSampleRates(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EllaiVoicePcm16Converter.ConvertToMonoPcm16([0f], 1, sampleRate));
    }

    [Fact]
    public void RejectsAudioFramesThatExceedTheBoundedOutputSize()
    {
        var input = new float[5_000];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EllaiVoicePcm16Converter.ConvertToMonoPcm16(input, 1, 8_000));
    }

    private static short[] DecodeSamples(byte[] pcm)
    {
        var samples = new short[pcm.Length / sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)));
        }
        return samples;
    }
}
