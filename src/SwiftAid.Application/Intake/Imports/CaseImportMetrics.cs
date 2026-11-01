using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SwiftAid.Application.Intake.Imports;

/// <summary>Bounded operational labels only; no source text, actor, organization or case identifiers.</summary>
internal static class CaseImportMetrics
{
    private static readonly Meter Meter = new("SwiftAid.Ingestion.Imports", "1.0.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("swiftaid.ingestion.import.duration", "ms");
    private static readonly Counter<long> Rows = Meter.CreateCounter<long>("swiftaid.ingestion.import.rows");

    public static IDisposable Measure(string stage) => new Measurement(stage switch
    {
        "batch" or "admission" or "validation" or "registration" or "checkpoint" => stage,
        _ => "other"
    });

    public static void RecordCommittedRow(string status)
    {
        var label = status switch
        {
            "Accepted" or "Replayed" or "Invalid" or "Denied" or "Conflict" or "RetryableFailure" or "Failed" => status,
            _ => "Other"
        };
        try { Rows.Add(1, new KeyValuePair<string, object?>("status", label)); }
        catch { /* A telemetry listener must not change a durable ingestion outcome. */ }
    }

    private sealed class Measurement(string stage) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            try { Duration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, new KeyValuePair<string, object?>("stage", stage)); }
            catch { /* Diagnostics must not interrupt accepted work or replay. */ }
        }
    }
}
