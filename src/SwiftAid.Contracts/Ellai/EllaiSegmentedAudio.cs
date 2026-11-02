using System.Buffers.Binary;

namespace SwiftAid.Contracts.Ellai;

public enum EllaiSpeechEventKind { Audio = 1, SpeechStarted = 2, SpeechEnded = 3, Heartbeat = 4 }

/// <summary>Session and capture identity are bound by the authenticated socket URI.
/// Positions count original mono 24 kHz samples, including suppressed silence.
/// Speech events are advisory and never authorize turn submission.</summary>
public readonly record struct EllaiSpeechEvent(
    EllaiSpeechEventKind Kind, long Sequence, long SamplePosition, long StreamEpoch)
{
    public const string Protocol = "swiftaid.voice.segmented.v1";
    public const int HeaderBytes = 32;
    public const int MaximumPcmBytes = 8192;
    public const long MaximumPosition = 24_000L * 60 * 10;

    public byte[] Encode(ReadOnlySpan<byte> pcm = default)
    {
        Validate(pcm.Length);
        var packet = new byte[HeaderBytes + pcm.Length];
        "ESV1"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), (int)Kind);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8), Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(16), SamplePosition);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(24), StreamEpoch);
        pcm.CopyTo(packet.AsSpan(HeaderBytes));
        return packet;
    }

    public static EllaiSpeechEvent Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderBytes || !packet[..4].SequenceEqual("ESV1"u8))
            throw new InvalidDataException("Unsupported voice frame.");
        var value = new EllaiSpeechEvent((EllaiSpeechEventKind)BinaryPrimitives.ReadInt32LittleEndian(packet[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(packet[8..]), BinaryPrimitives.ReadInt64LittleEndian(packet[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(packet[24..]));
        value.Validate(packet.Length - HeaderBytes);
        return value;
    }

    private void Validate(int pcmBytes)
    {
        if (!Enum.IsDefined(Kind) || Sequence < 1 || StreamEpoch < 1 || SamplePosition < 0
            || SamplePosition > MaximumPosition - pcmBytes / 2 || pcmBytes > MaximumPcmBytes
            || (pcmBytes & 1) != 0 || (Kind == EllaiSpeechEventKind.Audio ? pcmBytes == 0 : pcmBytes != 0))
            throw new InvalidDataException("Invalid voice frame metadata.");
    }
}
