using SwiftAid.Contracts.Events;

namespace SwiftAid.Application.Abstractions;

public enum EventPublishStatus
{
    AcknowledgedByJetStream,
    NotConfigured,
    Unavailable,
    Failed,
    Cancelled
}

public sealed record EventPublishResult(
    EventPublishStatus Status,
    string? StreamName = null,
    ulong? StreamSequence = null,
    string? FailureCode = null);

/// <summary>
/// Publishes versioned domain events through durable JetStream publication. A successful result means
/// JetStream acknowledged storage; it does not mean a consumer processed the event.
/// </summary>
public interface IEventPublisher
{
    ValueTask<EventPublishResult> PublishAsync<TPayload>(
        CaseEventEnvelope<TPayload> eventEnvelope,
        CancellationToken cancellationToken)
        where TPayload : CaseEventPayload;
}
