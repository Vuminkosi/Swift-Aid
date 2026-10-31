using System.Diagnostics.Metrics;

namespace SwiftAid.Application.Ellai;

/// <summary>Privacy-safe voice timing instruments. Measurements contain no
/// session identifiers, transcripts, audio, or provider payloads.</summary>
internal static class EllaiVoiceTurnMetrics
{
    private static readonly Meter Meter = new("SwiftAid.Application.Ellai.Voice", "0.1.0");
    private static readonly Histogram<double> SpeechStartDelay = Meter.CreateHistogram<double>(
        "swiftaid.ellai.voice.speech.start.delay", "ms",
        "Acoustic speech candidate duration before speech start confirmation.");
    private static readonly Histogram<double> EndpointSilence = Meter.CreateHistogram<double>(
        "swiftaid.ellai.voice.endpoint.silence", "ms",
        "Silence duration when the turn coordinator creates an endpoint candidate.");
    private static readonly Histogram<double> BargeInConfirmation = Meter.CreateHistogram<double>(
        "swiftaid.ellai.voice.barge_in.confirmation", "ms",
        "Consecutive acoustic speech duration before playback interruption is confirmed.");
    private static readonly Histogram<double> FinalTranscriptLatency = Meter.CreateHistogram<double>(
        "swiftaid.ellai.voice.transcription.final.latency", "ms",
        "Elapsed time from live stream start until an accepted provider final transcript.");
    private static readonly Counter<long> Recoveries = Meter.CreateCounter<long>(
        "swiftaid.ellai.voice.turn.recovery", "{turn}",
        "Live voice turns that entered recovery without an accepted final transcript.");

    public static void SpeechStartConfirmed(TimeSpan delay) => SafeRecord(SpeechStartDelay, delay.TotalMilliseconds);

    public static void EndpointCandidateReached(TimeSpan silence) =>
        SafeRecord(EndpointSilence, silence.TotalMilliseconds);

    public static void BargeInConfirmed(TimeSpan delay) =>
        SafeRecord(BargeInConfirmation, delay.TotalMilliseconds);

    public static void FinalTranscriptAccepted(TimeSpan latency) =>
        SafeRecord(FinalTranscriptLatency, latency.TotalMilliseconds);

    public static void TurnRecovered()
    {
        try
        {
            Recoveries.Add(1);
        }
        catch
        {
            // Instrumentation must not affect voice-session processing.
        }
    }

    private static void SafeRecord(Histogram<double> instrument, double value)
    {
        try
        {
            instrument.Record(value);
        }
        catch
        {
            // Instrumentation must not affect voice-session processing.
        }
    }
}
