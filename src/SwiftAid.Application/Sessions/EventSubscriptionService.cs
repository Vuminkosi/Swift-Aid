using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Application.Sessions;

public interface IEventSubscriptionStore
{
    Task<bool> IsAccountActiveAsync(string accountId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<EventSubscription?> GetAsync(Guid sessionId, string accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSubscription>> ListByAccountAsync(string accountId, CancellationToken cancellationToken);

    Task<EventSubscription> SubscribeAsync(
        Guid sessionId,
        string accountId,
        long expectedSessionVersion,
        long expectedSubscriptionVersion,
        string channel,
        string consentReference,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken);

    Task<EventSubscription> UnsubscribeAsync(
        Guid sessionId,
        string accountId,
        long expectedVersion,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken);
}

public interface IEventSubscriptionService
{
    Task<EventSubscriptionOptions?> GetOptionsAsync(Guid sessionId, string accountId, CancellationToken cancellationToken);

    Task<EventSubscription?> GetAsync(Guid sessionId, string accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSubscription>> ListMineAsync(string accountId, CancellationToken cancellationToken);

    Task<EventSubscription> SubscribeAsync(
        Guid sessionId,
        string accountId,
        long expectedSessionVersion,
        string consentReference,
        string channel,
        CancellationToken cancellationToken);

    Task<EventSubscription> UnsubscribeAsync(
        Guid sessionId,
        string accountId,
        long expectedSubscriptionVersion,
        CancellationToken cancellationToken);

    Task<bool> CanDispatchReminderAsync(
        Guid sessionId,
        string accountId,
        long sessionVersion,
        string consentReference,
        string channel,
        CancellationToken cancellationToken,
        long? expectedSubscriptionVersion = null);
}

/// <summary>Own-account-only subscription state. It never grants attendance or organization access.</summary>
public sealed class EventSubscriptionService(
    IEventSubscriptionStore store,
    ICommunitySessionService sessions,
    TimeProvider timeProvider) : IEventSubscriptionService
{
    private const string ConsentCopy = "Send me reminders for this public session through the selected channel. I can unsubscribe at any time.";
    private static readonly string[] Channels = ["Email", "MobilePush"];

    private readonly IEventSubscriptionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ICommunitySessionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<EventSubscriptionOptions?> GetOptionsAsync(
        Guid sessionId,
        string accountId,
        CancellationToken cancellationToken)
    {
        RequireIdentity(sessionId, accountId);
        var session = await _sessions.GetPublicAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!IsSubscribable(session)) return null;

        var current = await _store.GetAsync(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        return new EventSubscriptionOptions(sessionId, session!.Version, ConsentReference(sessionId, session.Version, current?.Version ?? 0),
            ConsentCopy, Array.AsReadOnly(Channels), IsCurrent: true);
    }

    public async Task<EventSubscription?> GetAsync(Guid sessionId, string accountId, CancellationToken cancellationToken)
    {
        RequireIdentity(sessionId, accountId);
        var session = await _sessions.GetPublicAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;

        var current = await _store.GetAsync(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        if (current is null) return new EventSubscription(sessionId, accountId, EventSubscriptionStatus.Unsubscribed, [], null, 0);
        if (current.Status == EventSubscriptionStatus.Active &&
            (!IsSubscribable(session) || !IsCurrentConsent(current.ConsentReference, sessionId, session.Version)))
        {
            return current with { Status = EventSubscriptionStatus.Unsubscribed, Channels = [], ConsentReference = null, HasInactiveStoredConsent = true };
        }

        return current;
    }

    public async Task<IReadOnlyList<EventSubscription>> ListMineAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        RequireAccount(accountId);
        var subscriptions = await _store.ListByAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        var results = new List<EventSubscription>(subscriptions.Count);
        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subscription.Status != EventSubscriptionStatus.Active)
            {
                results.Add(subscription);
                continue;
            }

            var session = await _sessions.GetPublicAsync(subscription.SessionId, cancellationToken).ConfigureAwait(false);
            if (!IsSubscribable(session) || !IsCurrentConsent(subscription.ConsentReference,
                    subscription.SessionId, session!.Version))
            {
                results.Add(subscription with
                {
                    Status = EventSubscriptionStatus.Unsubscribed,
                    Channels = [],
                    ConsentReference = null,
                    HasInactiveStoredConsent = true
                });
            }
            else
            {
                results.Add(subscription);
            }
        }

        return results;
    }

    public async Task<EventSubscription> SubscribeAsync(
        Guid sessionId,
        string accountId,
        long expectedSessionVersion,
        string consentReference,
        string channel,
        CancellationToken cancellationToken)
    {
        RequireIdentity(sessionId, accountId);
        if (!Channels.Contains(channel, StringComparer.Ordinal)) throw new ArgumentException("The reminder channel is unsupported.", nameof(channel));
        if (string.IsNullOrWhiteSpace(consentReference)) throw new ArgumentException("Explicit reminder consent is required.", nameof(consentReference));

        var session = await _sessions.GetPublicAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!IsSubscribable(session) || session!.Version != expectedSessionVersion ||
            !TryReadConsentVersion(consentReference, sessionId, session.Version, out var expectedSubscriptionVersion))
        {
            throw new SessionSubscriptionConflictException(sessionId);
        }

        return await _store.SubscribeAsync(sessionId, accountId, expectedSessionVersion, expectedSubscriptionVersion, channel, consentReference,
            _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public Task<EventSubscription> UnsubscribeAsync(
        Guid sessionId,
        string accountId,
        long expectedSubscriptionVersion,
        CancellationToken cancellationToken)
    {
        RequireIdentity(sessionId, accountId);
        if (expectedSubscriptionVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedSubscriptionVersion));
        return _store.UnsubscribeAsync(sessionId, accountId, expectedSubscriptionVersion,
            _timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<bool> CanDispatchReminderAsync(
        Guid sessionId,
        string accountId,
        long sessionVersion,
        string consentReference,
        string channel,
        CancellationToken cancellationToken,
        long? expectedSubscriptionVersion = null)
    {
        RequireIdentity(sessionId, accountId);
        if (!Channels.Contains(channel, StringComparer.Ordinal) ||
            !await _store.IsAccountActiveAsync(accountId, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            return false;
        var session = await _sessions.GetPublicAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!IsSubscribable(session) || session!.Version != sessionVersion ||
            !IsCurrentConsent(consentReference, sessionId, session.Version))
        {
            return false;
        }

        var subscription = await _store.GetAsync(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        return subscription is { Status: EventSubscriptionStatus.Active } &&
            (expectedSubscriptionVersion is null || subscription.Version == expectedSubscriptionVersion) &&
            string.Equals(subscription.ConsentReference, consentReference, StringComparison.Ordinal) &&
            subscription.Channels.Contains(channel, StringComparer.Ordinal);
    }

    private bool IsSubscribable(PublicSessionView? session) => session is { Status: "Scheduled" } &&
        session.StartUtc > _timeProvider.GetUtcNow();

    private static string ConsentReference(Guid sessionId, long sessionVersion, long subscriptionVersion) =>
        $"session-reminder-consent-v2:{sessionId:N}:{sessionVersion}:{subscriptionVersion}";

    private static bool IsCurrentConsent(string? reference, Guid sessionId, long sessionVersion) =>
        string.Equals(reference, $"session-reminder-consent-v1:{sessionId:N}:{sessionVersion}", StringComparison.Ordinal) ||
        TryReadConsentVersion(reference, sessionId, sessionVersion, out _);

    private static bool TryReadConsentVersion(string? reference, Guid sessionId, long sessionVersion, out long subscriptionVersion)
    {
        subscriptionVersion = 0;
        var prefix = $"session-reminder-consent-v2:{sessionId:N}:{sessionVersion}:";
        return reference is not null && reference.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(reference.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out subscriptionVersion) && subscriptionVersion >= 0 &&
            string.Equals(reference, ConsentReference(sessionId, sessionVersion, subscriptionVersion), StringComparison.Ordinal);
    }

    private static void RequireIdentity(Guid sessionId, string accountId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 200 || accountId != accountId.Trim() || accountId.Any(char.IsControl))
            throw new ArgumentException("An authenticated account is required.", nameof(accountId));
    }

    private static void RequireAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 200 ||
            accountId != accountId.Trim() || accountId.Any(char.IsControl))
            throw new ArgumentException("An authenticated account is required.", nameof(accountId));
    }
}

public sealed class SessionSubscriptionConflictException : InvalidOperationException
{
    private const string SafeMessage = "The session subscription changed; refresh and try again.";

    public SessionSubscriptionConflictException(Guid sessionId) : base(SafeMessage)
    {
        _ = sessionId;
    }

    public SessionSubscriptionConflictException(Guid sessionId, Exception innerException) : base(SafeMessage, innerException)
    {
        _ = sessionId;
    }
}
