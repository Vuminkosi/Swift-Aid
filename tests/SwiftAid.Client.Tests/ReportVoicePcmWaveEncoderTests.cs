using System.Buffers.Binary;
using SwiftAid.Client.Intake;

namespace SwiftAid.Client.Tests;

public sealed class ReportVoicePcmWaveEncoderTests
{
    [Fact]
    public void EncodesMonoPcmWithAConsistentWaveHeader()
    {
        byte[] samples = [0, 0, 0xff, 0x7f, 0, 0x80];

        var wave = ReportVoicePcmWaveEncoder.Encode(samples, 24000, 1);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24, 4)));
        Assert.Equal(samples.Length, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40, 4)));
        Assert.Equal(samples, wave[44..]);
    }

    [Fact]
    public void DownmixesStereoAndResamplesToTheVoiceRate()
    {
        byte[] stereo = [0xe8, 0x03, 0x18, 0xfc, 0xd0, 0x07, 0xd0, 0x07,
                         0x30, 0xf8, 0x30, 0xf8, 0, 0, 0, 0];

        var wave = ReportVoicePcmWaveEncoder.Encode(stereo, 48000, 2);

        Assert.Equal(48, wave.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44, 2)));
        Assert.Equal(-2000, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(46, 2)));
    }

    [Fact]
    public void RejectsIncompleteOrOversizedInput()
    {
        Assert.Throws<ArgumentException>(() => ReportVoicePcmWaveEncoder.Encode([1], 24000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportVoicePcmWaveEncoder.Encode([0, 0], 24000, 3));
        Assert.Throws<ArgumentException>(() => ReportVoicePcmWaveEncoder.Encode(new byte[2 * 8000 * 125 + 2], 8000, 1));
    }
}
