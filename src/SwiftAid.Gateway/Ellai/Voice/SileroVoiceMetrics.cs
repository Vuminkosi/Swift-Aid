using System.Diagnostics.Metrics;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Privacy-safe measurements for the Gateway VAD implementation.</summary>
internal static class SileroVoiceMetrics
{
    private static readonly Meter Meter = new("SwiftAid.Gateway.Ellai.Voice", "1.0.0");
    private static readonly Histogram<double> InferenceDuration = Meter.CreateHistogram<double>(
        "swiftaid.ellai.voice.vad.inference.duration", "ms",
        "Duration of a successful Silero ONNX inference for one audio frame.");
    private static readonly Counter<long> SequenceGaps = Meter.CreateCounter<long>(
        "swiftaid.ellai.voice.audio.frame.sequence.gap", "{frame}",
        "Audio frame sequence gaps that reset per-stream Silero recurrent state.");

    public static void InferenceCompleted(double elapsedMilliseconds)
    {
        try
        {
            InferenceDuration.Record(elapsedMilliseconds);
        }
        catch
        {
            // Telemetry must not affect live voice processing.
        }
    }

    public static void FrameSequenceGapDetected()
    {
        try
        {
            SequenceGaps.Add(1);
        }
        catch
        {
            // Telemetry must not affect live voice processing.
        }
    }
}
