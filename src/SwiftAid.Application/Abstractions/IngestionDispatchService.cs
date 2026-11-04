using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SwiftAid.Application.Abstractions;

/// <summary>One bounded publication attempt; cancellation leaves work recoverable by lease expiry.</summary>
public sealed class IngestionDispatchService(IIngestionOutbox outbox, IIngestionNoticePublisher publisher, TimeProvider clock)
{
    public const string InstrumentationName = "SwiftAid.Ingestion";
    private static readonly Meter Meter = new(InstrumentationName);
    private static readonly ActivitySource Activities = new(InstrumentationName);
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("swiftaid.ingestion.publish.outcomes");
    private static readonly Histogram<double> QueueAge = Meter.CreateHistogram<double>("swiftaid.ingestion.outbox.age", "s");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("swiftaid.ingestion.publish.duration", "ms");

    public async Task<bool> DispatchOneAsync(string sourceKind, CancellationToken cancellationToken)
    {
        var leases = await outbox.ClaimAsync(sourceKind, clock.GetUtcNow(), TimeSpan.FromMinutes(1), 1, cancellationToken);
        if (leases.Count == 0) return false;
        var lease = leases[0];
        using var activity = Activities.StartActivity("ingestion.publish", ActivityKind.Producer);
        activity?.SetTag("messaging.message.id", lease.Notice.MessageId.ToString("D"));
        activity?.SetTag("ingestion.source.kind", sourceKind);
        QueueAge.Record(Math.Max(0, (clock.GetUtcNow() - lease.Notice.RecordedAtUtc).TotalSeconds), new KeyValuePair<string, object?>("source.kind", sourceKind));
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        EventPublishResult result;
        try
        {
            result = await publisher.PublishAsync(lease.Notice, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = new(EventPublishStatus.Cancelled, FailureCode: "publish-timeout");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = new(EventPublishStatus.Failed, FailureCode: "publish-failed");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var acknowledged = result.Status == EventPublishStatus.AcknowledgedByJetStream &&
            result.StreamName == Contracts.Events.IngestionNotice.StreamName && result.StreamSequence > 0;
        var updated = acknowledged
            ? await outbox.CompleteAsync(lease, clock.GetUtcNow(), cancellationToken)
            : await outbox.FailAsync(lease, clock.GetUtcNow(), SafeFailure(result.FailureCode), cancellationToken);
        var outcome = !updated ? "lease-lost" : acknowledged ? "published" : lease.Attempt >= 8 ? "exhausted" : "retrying";
        Outcomes.Add(1, new("source.kind", sourceKind), new("outcome", outcome));
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new("source.kind", sourceKind), new("outcome", outcome));
        activity?.SetTag("ingestion.outcome", outcome);
        return true;
    }

    private static string SafeFailure(string? code) => code is "publish-timeout" or "jetstream-unavailable" or
        "invalid-notice" or "unexpected-stream" ? code : "publish-failed";
}
