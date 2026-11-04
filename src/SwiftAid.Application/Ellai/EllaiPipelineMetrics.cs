using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SwiftAid.Application.Ellai;

/// <summary>Stage latency only. No prompt, transcript, audio, actor, or case data is recorded.</summary>
public static class EllaiPipelineMetrics
{
    private static readonly Meter Meter = new("SwiftAid.Ellai.Pipeline", "1.0.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "swiftaid.ellai.pipeline.duration", "ms");

    public static IDisposable Measure(string stage) => new Measurement(stage);

    public static void Record(string stage, double milliseconds)
    {
        try { Duration.Record(Math.Max(0, milliseconds), new KeyValuePair<string, object?>("stage", stage)); }
        catch { /* Diagnostics must not interrupt accepted work. */ }
    }

    private sealed class Measurement(string stage) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose() => Record(stage, Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
    }
}
