using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Notifications;
using SwiftAid.Domain.Notifications;

namespace SwiftAid.Infrastructure.Persistence.Notifications;

/// <summary>Atomically leases and advances durable notification dispatch records in PostgreSQL.</summary>
public sealed class PostgresNotificationDeliveryOutbox(SwiftAidDbContext dbContext)
    : INotificationDeliveryOutbox
{
    private const int MaximumAttempts = 8;
    private const int MaximumBatchSize = 100;
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromDays(1);

    public async ValueTask<IReadOnlyList<NotificationDeliveryLease>> ClaimDueAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var now = nowUtc.ToUniversalTime();
        var leaseUntil = now + leaseDuration;
        await dbContext.NotificationDeliveryOutbox
            .Where(row => row.PublishedAtUtc == null &&
                          row.ExhaustedAtUtc == null &&
                          row.AttemptCount >= MaximumAttempts &&
                          (row.LeaseUntilUtc == null || row.LeaseUntilUtc <= now))
            .ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.ExhaustedAtUtc, now)
                    .SetProperty(row => row.LeaseId, (Guid?)null)
                    .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false);

        var rows = await dbContext.Set<NotificationDeliveryOutboxEntity>()
            .FromSqlInterpolated($"""
                WITH due AS (
                    SELECT outbox_id
                    FROM swiftaid_notifications.notification_delivery_outbox
                    WHERE published_at_utc IS NULL
                      AND exhausted_at_utc IS NULL
                      AND available_at_utc <= {now}
                      AND (lease_until_utc IS NULL OR lease_until_utc <= {now})
                      AND attempt_count < {MaximumAttempts}
                    ORDER BY available_at_utc, created_at_utc, outbox_id
                    FOR UPDATE SKIP LOCKED
                    LIMIT {batchSize}
                )
                UPDATE swiftaid_notifications.notification_delivery_outbox AS outbox
                SET attempt_count = outbox.attempt_count + 1,
                    lease_id = gen_random_uuid(),
                    lease_until_utc = {leaseUntil}
                FROM due
                WHERE outbox.outbox_id = due.outbox_id
                RETURNING outbox.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return Array.Empty<NotificationDeliveryLease>();
        }

        var notificationIds = rows.Select(row => row.NotificationId).ToArray();
        var notifications = await dbContext.NotificationItems
            .AsNoTracking()
            .Where(item => notificationIds.Contains(item.NotificationId))
            .ToDictionaryAsync(item => item.NotificationId, cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(row =>
            {
                if (!notifications.TryGetValue(row.NotificationId, out var notification) ||
                    row.LeaseId is not Guid leaseId ||
                    row.LeaseUntilUtc is not DateTimeOffset leaseExpiry)
                {
                    throw new InvalidOperationException("A claimed notification delivery row is incomplete.");
                }

                return new NotificationDeliveryLease(
                    row.OutboxId,
                    row.NotificationId,
                    notification.AcceptedEventId,
                    notification.RecipientActorId,
                    notification.ResourceKind,
                    notification.ResourceId,
                    notification.Action,
                    notification.RecipientRequirement,
                    row.AttemptCount,
                    leaseId,
                    leaseExpiry);
            })
            .ToArray();
    }

    public async ValueTask<NotificationDeliveryOutboxUpdateStatus> AcknowledgeAsync(
        Guid outboxId,
        Guid leaseId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (outboxId == Guid.Empty || leaseId == Guid.Empty)
        {
            return NotificationDeliveryOutboxUpdateStatus.NotFoundOrLeaseLost;
        }

        var now = nowUtc.ToUniversalTime();
        var updated = await dbContext.NotificationDeliveryOutbox
            .Where(row => row.OutboxId == outboxId &&
                          row.LeaseId == leaseId &&
                          row.LeaseUntilUtc > now &&
                          row.PublishedAtUtc == null &&
                          row.ExhaustedAtUtc == null)
            .ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.PublishedAtUtc, now)
                    .SetProperty(row => row.LeaseId, (Guid?)null)
                    .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false);

        return updated == 1
            ? NotificationDeliveryOutboxUpdateStatus.Updated
            : NotificationDeliveryOutboxUpdateStatus.NotFoundOrLeaseLost;
    }

    public async ValueTask<NotificationDeliveryOutboxUpdateStatus> RetryAsync(
        Guid outboxId,
        Guid leaseId,
        DateTimeOffset nowUtc,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        if (outboxId == Guid.Empty || leaseId == Guid.Empty)
        {
            return NotificationDeliveryOutboxUpdateStatus.NotFoundOrLeaseLost;
        }

        if (retryDelay < TimeSpan.Zero || retryDelay > MaximumRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        var now = nowUtc.ToUniversalTime();
        var row = await dbContext.NotificationDeliveryOutbox
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OutboxId == outboxId &&
                                               candidate.LeaseId == leaseId &&
                                               candidate.LeaseUntilUtc > now &&
                                               candidate.PublishedAtUtc == null &&
                                               candidate.ExhaustedAtUtc == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return NotificationDeliveryOutboxUpdateStatus.NotFoundOrLeaseLost;
        }

        if (row.AttemptCount >= MaximumAttempts)
        {
            var exhausted = await dbContext.NotificationDeliveryOutbox
                .Where(candidate => candidate.OutboxId == outboxId &&
                                    candidate.LeaseId == leaseId &&
                                    candidate.LeaseUntilUtc > now)
                .ExecuteUpdateAsync(update => update
                        .SetProperty(candidate => candidate.ExhaustedAtUtc, now)
                        .SetProperty(candidate => candidate.LeaseId, (Guid?)null)
                        .SetProperty(candidate => candidate.LeaseUntilUtc, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            return exhausted == 1
                ? NotificationDeliveryOutboxUpdateStatus.Exhausted
                : NotificationDeliveryOutboxUpdateStatus.NotFoundOrLeaseLost;
        }

        var availableAt = now + retryDelay;
        var updated = await dbContext.NotificationDeliveryOutbox
            .Where(candidate => candidate.OutboxId == outboxId &&
                                candidate.LeaseId == leaseId &&
                                candidate.LeaseUntilUtc > now)
            .ExecuteUpdateAsync(update => update
                    .SetProperty(candidate => candidate.AvailableAtUtc, availableAt)
                    .SetProperty(candidate => candidate.LeaseId, (Guid?)null)
                    .SetProperty(candidate => candidate.LeaseUntilUtc, (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1
            ? NotificationDeliveryOutboxUpdateStatus.Updated
            : NotificationDeliveryOutboxUpdateStatus.NotFoundOrLeaseLost;
    }
}
