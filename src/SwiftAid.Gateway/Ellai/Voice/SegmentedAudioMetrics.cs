using System.Diagnostics.Metrics;

namespace SwiftAid.Gateway.Ellai;

internal static class SegmentedAudioMetrics
{
    private static readonly Meter Meter = new("SwiftAid.Ellai.SegmentedAudio", "1.0");
    private static readonly Counter<long> Audio = Meter.CreateCounter<long>("ellai.voice.audio.bytes", "By");
    private static readonly Counter<long> Silence = Meter.CreateCounter<long>("ellai.voice.suppressed.samples", "{sample}");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("ellai.voice.duplicate.events", "{event}");
    private static readonly Counter<long> Missing = Meter.CreateCounter<long>("ellai.voice.missing.events", "{event}");
    public static void Record(long bytes, long suppressed, long duplicates, long missing)
    {
        try { Audio.Add(bytes); Silence.Add(suppressed); Duplicates.Add(duplicates); Missing.Add(missing); }
        catch { /* Diagnostics must never break capture. */ }
    }
}
