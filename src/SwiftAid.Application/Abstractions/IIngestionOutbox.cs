using SwiftAid.Contracts.Events;

namespace SwiftAid.Application.Abstractions;

public sealed record IngestionDeliveryLease(
    Guid OutboxId, Guid LeaseId, int Attempt, DateTimeOffset LeaseUntilUtc, IngestionNotice Notice);

/// <summary>Existing report/case outboxes, leased independently from notification and Ellai work.</summary>
public interface IIngestionOutbox
{
    ValueTask<IReadOnlyList<IngestionDeliveryLease>> ClaimAsync(
        string sourceKind, DateTimeOffset now, TimeSpan leaseDuration, int batchSize, CancellationToken cancellationToken);

    ValueTask<bool> CompleteAsync(IngestionDeliveryLease lease, DateTimeOffset now, CancellationToken cancellationToken);

    ValueTask<bool> FailAsync(IngestionDeliveryLease lease, DateTimeOffset now, string failureCode, CancellationToken cancellationToken);
}

public interface IIngestionNoticePublisher
{
    ValueTask<EventPublishResult> PublishAsync(IngestionNotice notice, CancellationToken cancellationToken);
}
