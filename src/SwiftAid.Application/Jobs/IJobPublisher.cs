using SwiftAid.Contracts.Jobs;

namespace SwiftAid.Application.Jobs;

public enum JobPublishStatus
{
    AcknowledgedByJetStream,
    Unavailable,
    Failed,
    Cancelled
}

/// <summary>A minimized publication result; acknowledgement does not mean that a worker completed the job.</summary>
public sealed record PublishReceipt(
    JobPublishStatus Status,
    string? StreamName = null,
    ulong? StreamSequence = null,
    string? FailureCode = null);

/// <summary>Publishes an opaque, versioned job envelope to its configured durable JetStream subject.</summary>
public interface IJobPublisher
{
    ValueTask<PublishReceipt> PublishAsync(
        JobEnvelope envelope,
        CancellationToken cancellationToken);
}
