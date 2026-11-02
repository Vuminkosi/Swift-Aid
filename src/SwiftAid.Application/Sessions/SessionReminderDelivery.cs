namespace SwiftAid.Application.Sessions;

public enum SessionReminderDeliveryStatus
{
    Pending,
    Submitted,
    Suppressed,
    OutcomeUnknown
}

public sealed record SessionReminderDeliveryLease(
    Guid DeliveryId,
    Guid OccurrenceId,
    Guid SessionId,
    string AccountId,
    string Channel,
    long SessionVersion,
    long SubscriptionVersion,
    string ConsentReference,
    int AttemptCount,
    Guid LeaseId,
    DateTimeOffset LeaseUntilUtc);

/// <summary>Durable, account-scoped work generated from due public-session reminder occurrences.</summary>
public interface ISessionReminderDeliveryStore
{
    Task<int> MaterializeDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionReminderDeliveryLease>> ClaimDueAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int limit,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        Guid deliveryId,
        Guid leaseId,
        SessionReminderDeliveryStatus status,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<bool> RetryAsync(
        Guid deliveryId,
        Guid leaseId,
        DateTimeOffset availableAtUtc,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}
