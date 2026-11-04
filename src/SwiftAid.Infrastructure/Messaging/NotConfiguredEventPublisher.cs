using SwiftAid.Application.Abstractions;
using SwiftAid.Contracts.Events;

namespace SwiftAid.Infrastructure.Messaging;

public sealed class NotConfiguredEventPublisher : IEventPublisher
{
    public ValueTask<EventPublishResult> PublishAsync<TPayload>(
        CaseEventEnvelope<TPayload> eventEnvelope,
        CancellationToken cancellationToken)
        where TPayload : CaseEventPayload =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new EventPublishResult(EventPublishStatus.Cancelled)
            : new EventPublishResult(EventPublishStatus.NotConfigured));
}
