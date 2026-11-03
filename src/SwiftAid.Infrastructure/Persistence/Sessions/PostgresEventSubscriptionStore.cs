using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>PostgreSQL store for private event subscriptions; the session/account key is unique.</summary>
public sealed class PostgresEventSubscriptionStore(SwiftAidDbContext dbContext) : IEventSubscriptionStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public Task<bool> IsAccountActiveAsync(string accountId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        (from actor in _dbContext.Actors.AsNoTracking()
         join accessState in _dbContext.ActorAccessStates.AsNoTracking() on actor.ActorId equals accessState.ActorId
         where actor.ActorId == accountId && actor.Status == PersistentActorStatus.Active &&
               (actor.ExpiresAtUtc == null || actor.ExpiresAtUtc > nowUtc) && accessState.AccessEpoch >= 0
         select actor.ActorId).AnyAsync(cancellationToken);

    public async Task<EventSubscription?> GetAsync(Guid sessionId, string accountId, CancellationToken cancellationToken)
    {
        var entity = await Subscriptions().AsNoTracking()
            .SingleOrDefaultAsync(item => item.SessionId == sessionId && item.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToContract(entity);
    }

    public async Task<IReadOnlyList<EventSubscription>> ListByAccountAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var entities = await Subscriptions().AsNoTracking()
            .Where(item => item.AccountId == accountId)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.SessionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(ToContract).ToArray();
    }

    public async Task<EventSubscription> SubscribeAsync(
        Guid sessionId,
        string accountId,
        long expectedSessionVersion,
        string channel,
        string consentReference,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var affected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH eligible_session AS (
                SELECT session_id FROM community_sessions
                WHERE session_id = {sessionId} AND version = {expectedSessionVersion}
                  AND visibility = 'Public' AND status = 'Scheduled'
                  AND published_version > 0 AND scheduled_at_utc IS NOT NULL
                  AND start_utc > {recordedAtUtc}
                FOR UPDATE
            )
            INSERT INTO community_session_subscriptions
                (session_id, account_id, channel, consent_reference, status, version, updated_at_utc)
            SELECT session_id, {accountId}, {channel}, {consentReference}, 'Active', 1, {recordedAtUtc}
            FROM eligible_session
            ON CONFLICT (session_id, account_id) DO UPDATE SET
                channel = EXCLUDED.channel,
                consent_reference = EXCLUDED.consent_reference,
                status = 'Active',
                version = CASE
                    WHEN community_session_subscriptions.status <> 'Active'
                      OR community_session_subscriptions.channel <> EXCLUDED.channel
                      OR community_session_subscriptions.consent_reference <> EXCLUDED.consent_reference
                    THEN community_session_subscriptions.version + 1
                    ELSE community_session_subscriptions.version
                END,
                updated_at_utc = CASE
                    WHEN community_session_subscriptions.status <> 'Active'
                      OR community_session_subscriptions.channel <> EXCLUDED.channel
                      OR community_session_subscriptions.consent_reference <> EXCLUDED.consent_reference
                    THEN EXCLUDED.updated_at_utc
                    ELSE community_session_subscriptions.updated_at_utc
                END
            """, cancellationToken).ConfigureAwait(false);

        if (affected == 0)
            throw new SessionSubscriptionConflictException(sessionId);

        var saved = await GetAsync(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        return saved ?? throw new InvalidOperationException("The event subscription was not persisted.");
    }

    public async Task<EventSubscription> UnsubscribeAsync(
        Guid sessionId,
        string accountId,
        long expectedVersion,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var entity = await Subscriptions()
            .SingleOrDefaultAsync(item => item.SessionId == sessionId && item.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            if (expectedVersion == 0)
                return new EventSubscription(sessionId, accountId, EventSubscriptionStatus.Unsubscribed, [], null, 0);
            throw new SessionSubscriptionConflictException(sessionId);
        }

        if (entity.Status == nameof(EventSubscriptionStatus.Unsubscribed)) return ToContract(entity);
        if (entity.Version != expectedVersion) throw new SessionSubscriptionConflictException(sessionId);

        entity.Status = nameof(EventSubscriptionStatus.Unsubscribed);
        entity.Version++;
        entity.UpdatedAtUtc = recordedAtUtc;
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new SessionSubscriptionConflictException(sessionId, exception);
        }

        return ToContract(entity);
    }

    private DbSet<CommunitySessionSubscriptionEntity> Subscriptions() => _dbContext.CommunitySessionSubscriptions;

    private static EventSubscription ToContract(CommunitySessionSubscriptionEntity entity) =>
        new(entity.SessionId, entity.AccountId, Enum.Parse<EventSubscriptionStatus>(entity.Status),
            entity.Status == nameof(EventSubscriptionStatus.Active) ? [entity.Channel] : [],
            entity.Status == nameof(EventSubscriptionStatus.Active) ? entity.ConsentReference : null,
            entity.Version);
}
