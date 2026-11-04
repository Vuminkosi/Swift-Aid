using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiSilenceGateTests
{
    [Fact]
    public void MinuteOfSilenceUploadsNoPcmAndNextSpeechIncludesPreroll()
    {
        using var gate = new EllaiSilenceGate(1);
        var pcm = new byte[1536];
        for (var i = 0; i < 1875; i++) Assert.Empty(gate.Push(pcm, 0));
        Assert.Equal(0, gate.AudioBytes);
        var heartbeat = EllaiSpeechEvent.Decode(gate.Heartbeat());
        Assert.Equal(1_440_000, heartbeat.SamplePosition);
        var packets = gate.Push(pcm, 0.9f);
        Assert.Equal(EllaiSpeechEventKind.SpeechStarted, EllaiSpeechEvent.Decode(packets[0]).Kind);
        Assert.Equal(1_421_568, EllaiSpeechEvent.Decode(packets[1]).SamplePosition);
        Assert.Equal(25 * 1536, gate.AudioBytes);
    }

    [Fact]
    public void ShortPauseKeepsSpeechOpenAndLongPauseStopsPcmOnly()
    {
        using var gate = new EllaiSilenceGate(7);
        var pcm = new byte[1536];
        gate.Push(pcm, 0.9f);
        for (var i = 0; i < 15; i++) Assert.DoesNotContain(gate.Push(pcm, 0), p => EllaiSpeechEvent.Decode(p).Kind == EllaiSpeechEventKind.SpeechEnded);
        gate.Push(pcm, 0.9f);
        var ended = 0;
        for (var i = 0; i < 313; i++) ended += gate.Push(pcm, 0).Count(p => EllaiSpeechEvent.Decode(p).Kind == EllaiSpeechEventKind.SpeechEnded);
        Assert.Equal(1, ended);
        var before = gate.AudioBytes;
        for (var i = 0; i < 100; i++) Assert.Empty(gate.Push(pcm, 0));
        Assert.Equal(before, gate.AudioBytes);
        Assert.NotEmpty(gate.Push(pcm, 0.9f));
    }

    [Fact]
    public void LocalInterruptionNeedsConfirmedSpeechAndEventsRemainOrdered()
    {
        using var gate = new EllaiSilenceGate(8);
        var confirmed = 0;
        gate.SpeechConfirmed += () => confirmed++;
        var packets = new List<byte[]>();
        for (var i = 0; i < 5; i++) packets.AddRange(gate.Push(new byte[1536], 0.9f));
        Assert.Equal(0, confirmed);
        packets.AddRange(gate.Push(new byte[1536], 0.9f));
        Assert.Equal(1, confirmed);
        Assert.Equal(Enumerable.Range(1, packets.Count).Select(i => (long)i), packets.Select(p => EllaiSpeechEvent.Decode(p).Sequence));
    }

    [Fact]
    public void InvalidFramesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => EllaiSpeechEvent.Decode(new byte[32]));
        Assert.Throws<InvalidDataException>(() => new EllaiSpeechEvent(EllaiSpeechEventKind.Audio, 1, 0, 1).Encode(new byte[3]));
        Assert.Throws<InvalidDataException>(() => new EllaiSpeechEvent(EllaiSpeechEventKind.Heartbeat, 1, -1, 1).Encode());
    }

    [Fact]
    public void CaptureRejectsFramesPastProtocolPositionLimitWithoutAdvancing()
    {
        using var gate = new EllaiSilenceGate(1, EllaiSpeechEvent.MaximumPosition - 768);
        var pcm = new byte[1536];

        Assert.Empty(gate.Push(pcm, 0));
        Assert.Equal(EllaiSpeechEvent.MaximumPosition, gate.Position);
        Assert.Equal(EllaiSpeechEvent.MaximumPosition, EllaiSpeechEvent.Decode(gate.Heartbeat()).SamplePosition);

        Assert.Throws<InvalidDataException>(() => gate.Push(pcm, 0));
        Assert.Equal(EllaiSpeechEvent.MaximumPosition, gate.Position);
    }
}
