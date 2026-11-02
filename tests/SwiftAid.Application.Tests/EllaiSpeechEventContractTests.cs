using System.Buffers.Binary;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests;

public sealed class EllaiSpeechEventContractTests
{
    [Fact]
    public void AudioFrameAtMaximumPermittedEndPositionRoundTrips()
    {
        var value = new EllaiSpeechEvent(
            EllaiSpeechEventKind.Audio,
            Sequence: 1,
            SamplePosition: EllaiSpeechEvent.MaximumPosition - 1,
            StreamEpoch: 1);

        var decoded = EllaiSpeechEvent.Decode(value.Encode([0, 0]));

        Assert.Equal(EllaiSpeechEvent.MaximumPosition - 1, decoded.SamplePosition);
    }

    [Theory]
    [InlineData(14400000L)]
    [InlineData(long.MaxValue)]
    public void AudioFramePastMaximumPositionIsRejectedWithoutArithmeticOverflow(long samplePosition)
    {
        var value = new EllaiSpeechEvent(EllaiSpeechEventKind.Audio, 1, samplePosition, 1);

        Assert.Throws<InvalidDataException>(() => value.Encode([0, 0]));
    }

    [Fact]
    public void DecodeRejectsOverflowingSamplePosition()
    {
        var packet = new EllaiSpeechEvent(EllaiSpeechEventKind.Audio, 1, 0, 1).Encode([0, 0]);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(16), long.MaxValue);

        Assert.Throws<InvalidDataException>(() => EllaiSpeechEvent.Decode(packet));
    }
}
