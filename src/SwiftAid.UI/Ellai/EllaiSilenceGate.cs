using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai;

/// <summary>Packet selection only; speech probability comes from Silero, never RMS.
/// Keeps 800 ms pre-roll and 704 ms of real captured trailing audio.</summary>
public sealed class EllaiSilenceGate(long epoch, long initialPosition = 0) : IDisposable
{
    private readonly Queue<(long Position, byte[] Pcm)> _preRoll = new();
    private int _bufferedSamples;
    private int _quietSamples;
    private int _speechSamples;
    private bool _speaking;
    private long _sequence;
    private readonly long _initialPosition = initialPosition;
    public long Position { get; private set; } = initialPosition;
    public long AudioBytes { get; private set; }
    public long SuppressedSamples => Position - _initialPosition - AudioBytes / 2 - _bufferedSamples;
    public event Action? SpeechConfirmed;

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> pcm, float probability)
    {
        if (pcm.Length == 0 || pcm.Length > EllaiSpeechEvent.MaximumPcmBytes || (pcm.Length & 1) != 0
            || !float.IsFinite(probability) || probability is < 0 or > 1)
            throw new ArgumentException("Invalid Silero frame.");
        var sampleCount = pcm.Length / 2;
        if (Position < 0 || sampleCount > EllaiSpeechEvent.MaximumPosition
            || Position > EllaiSpeechEvent.MaximumPosition - sampleCount)
            throw new InvalidDataException("Voice capture exceeded the segmented-audio position limit.");
        var packets = new List<byte[]>();
        var start = Position;
        Position += sampleCount;
        var positive = probability >= (_speaking ? 0.25f : 0.5f);
        if (!_speaking)
        {
            _preRoll.Enqueue((start, pcm.ToArray()));
            _bufferedSamples += pcm.Length / 2;
            while (_bufferedSamples > 19_200 && _preRoll.Count > 1)
            {
                var old = _preRoll.Dequeue();
                _bufferedSamples -= old.Pcm.Length / 2;
                Array.Clear(old.Pcm);
            }
            if (!positive) return packets;
            _speaking = true;
            packets.Add(Event(EllaiSpeechEventKind.SpeechStarted, _preRoll.Peek().Position));
            while (_preRoll.TryDequeue(out var frame))
            {
                packets.Add(Audio(frame.Position, frame.Pcm));
                Array.Clear(frame.Pcm);
            }
            _bufferedSamples = 0;
        }
        else packets.Add(Audio(start, pcm));

        _speechSamples = positive ? _speechSamples + pcm.Length / 2 : 0;
        if (_speechSamples >= 4608 && _speechSamples - pcm.Length / 2 < 4608) SpeechConfirmed?.Invoke();
        _quietSamples = positive ? 0 : _quietSamples + pcm.Length / 2;
        if (_quietSamples >= 16_800)
        {
            packets.Add(Event(EllaiSpeechEventKind.SpeechEnded, Position));
            _speaking = false;
            _quietSamples = _speechSamples = 0;
        }
        return packets;
    }

    public byte[] Heartbeat() => Event(EllaiSpeechEventKind.Heartbeat, Position);
    private byte[] Event(EllaiSpeechEventKind kind, long position) => new EllaiSpeechEvent(kind, ++_sequence, position, epoch).Encode();
    private byte[] Audio(long position, ReadOnlySpan<byte> pcm)
    {
        AudioBytes += pcm.Length;
        return new EllaiSpeechEvent(EllaiSpeechEventKind.Audio, ++_sequence, position, epoch).Encode(pcm);
    }
    public void Dispose()
    {
        while (_preRoll.TryDequeue(out var frame)) Array.Clear(frame.Pcm);
    }
}
