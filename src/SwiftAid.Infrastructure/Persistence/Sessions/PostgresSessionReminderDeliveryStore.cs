using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Sessions;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>PostgreSQL materialization and leasing for account-scoped session reminder work.</summary>
public sealed class PostgresSessionReminderDeliveryStore(SwiftAidDbContext dbContext) : ISessionReminderDeliveryStore
{
    private const int MaximumBatchSize = 100;
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public Task<int> MaterializeDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
    {
        ValidateBatch(limit);
        var now = nowUtc.ToUniversalTime();
        return _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.session_reminder_deliveries
                (delivery_id, occurrence_id, session_id, account_id, channel, session_version,
                 subscription_version, consent_reference, status, available_at_utc, lease_id,
                 lease_until_utc, attempt_count, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), occurrence.occurrence_id, session.session_id, subscription.account_id,
                   subscription.channel, occurrence.version, subscription.version, subscription.consent_reference,
                   'Pending', {now}, NULL, NULL, 0, {now}, {now}
            FROM public.session_reminder_occurrences AS occurrence
            JOIN public.community_sessions AS session ON session.session_id = occurrence.session_id
            JOIN public.community_session_subscriptions AS subscription
              ON subscription.session_id = session.session_id
            JOIN public.actors AS actor ON actor.actor_id = subscription.account_id
            JOIN public.actor_access_states AS access_state ON access_state.actor_id = actor.actor_id
            WHERE occurrence.status = 'Pending'
              AND occurrence.remind_at_utc <= {now}
              AND session.status = 'Scheduled'
              AND session.visibility = 'Public'
              AND session.published_version > 0
              AND session.version = occurrence.version
              AND session.start_utc > {now}
              AND subscription.status = 'Active'
              AND subscription.consent_reference =
                  'session-reminder-consent-v1:' || replace(session.session_id::text, '-', '') || ':' || session.version::text
              AND actor.status = 'Active'
              AND (actor.expires_at_utc IS NULL OR actor.expires_at_utc > {now})
              AND access_state.access_epoch >= 0
              AND NOT EXISTS (
                  SELECT 1 FROM public.session_reminder_deliveries AS existing
                  WHERE existing.occurrence_id = occurrence.occurrence_id
                    AND existing.account_id = subscription.account_id
                    AND existing.subscription_version = subscription.version
                    AND existing.channel = subscription.channel)
            ORDER BY occurrence.remind_at_utc, occurrence.occurrence_id, subscription.account_id
            LIMIT {limit}
            ON CONFLICT (occurrence_id, account_id, subscription_version, channel) DO NOTHING
            """, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionReminderDeliveryLease>> ClaimDueAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateBatch(limit);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var now = nowUtc.ToUniversalTime();
        var leaseUntil = now + leaseDuration;
        var claimed = await _dbContext.SessionReminderDeliveries
            .FromSqlInterpolated($"""
                WITH due AS (
                    SELECT delivery_id
                    FROM public.session_reminder_deliveries
                    WHERE status = 'Pending'
                      AND available_at_utc <= {now}
                      AND (lease_until_utc IS NULL OR lease_until_utc <= {now})
                    ORDER BY available_at_utc, delivery_id
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE public.session_reminder_deliveries AS delivery
                SET lease_id = gen_random_uuid(), lease_until_utc = {leaseUntil},
                    attempt_count = delivery.attempt_count + 1, updated_at_utc = {now}
                FROM due
                WHERE delivery.delivery_id = due.delivery_id
                RETURNING delivery.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return claimed.Select(item => new SessionReminderDeliveryLease(
            item.DeliveryId,
            item.OccurrenceId,
            item.SessionId,
            item.AccountId,
            item.Channel,
            item.SessionVersion,
            item.SubscriptionVersion,
            item.ConsentReference,
            item.AttemptCount,
            item.LeaseId ?? throw new InvalidOperationException("A claimed reminder delivery has no lease."),
            item.LeaseUntilUtc ?? throw new InvalidOperationException("A claimed reminder delivery has no lease expiry.")))
            .ToArray();
    }

    public async Task<bool> CompleteAsync(
        Guid deliveryId,
        Guid leaseId,
        SessionReminderDeliveryStatus status,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty || leaseId == Guid.Empty ||
            status is SessionReminderDeliveryStatus.Pending)
            throw new ArgumentException("A terminal reminder-delivery outcome and valid lease are required.");

        var now = nowUtc.ToUniversalTime();
        var affected = await _dbContext.SessionReminderDeliveries
            .Where(item => item.DeliveryId == deliveryId && item.LeaseId == leaseId && item.Status == "Pending")
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, status.ToString())
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<bool> RetryAsync(
        Guid deliveryId,
        Guid leaseId,
        DateTimeOffset availableAtUtc,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty || leaseId == Guid.Empty)
            throw new ArgumentException("A valid reminder-delivery lease is required.");

        var now = nowUtc.ToUniversalTime();
        var availableAt = availableAtUtc.ToUniversalTime();
        var affected = await _dbContext.SessionReminderDeliveries
            .Where(item => item.DeliveryId == deliveryId && item.LeaseId == leaseId && item.Status == "Pending")
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.AvailableAtUtc, availableAt)
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    private static void ValidateBatch(int limit)
    {
        if (limit is < 1 or > MaximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
    }
}
