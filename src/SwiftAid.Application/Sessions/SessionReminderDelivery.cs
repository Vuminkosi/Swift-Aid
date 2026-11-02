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
        IReadOnlyCollection<string> allowedChannels,
        CancellationToken cancellationToken);

    Task<bool> IsLeaseCurrentAsync(Guid deliveryId, Guid leaseId, CancellationToken cancellationToken);

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

public enum SessionReminderSendStatus
{
    Accepted,
    NotConfigured,
    NoRoute,
    OutcomeUnknown
}

public sealed class SessionReminderPreflightException : Exception
{
    public SessionReminderPreflightException(Exception innerException)
        : base("Session reminder preflight is temporarily unavailable.", innerException)
    {
    }
}

public interface ISessionReminderChannelSender
{
    Task<SessionReminderSendStatus> SendAsync(
        SessionReminderDeliveryLease delivery,
        CancellationToken cancellationToken);
}

/// <summary>Rechecks current consent and records provider outcomes without retrying ambiguous sends.</summary>
public sealed class SessionReminderDeliveryDispatcher(
    ISessionReminderDeliveryStore store,
    IEventSubscriptionService subscriptions,
    ISessionReminderChannelSender sender)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private const int BatchSize = 25;
    private const int MaximumBackoffSeconds = 3600;

    private readonly ISessionReminderDeliveryStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IEventSubscriptionService _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
    private readonly ISessionReminderChannelSender _sender = sender ?? throw new ArgumentNullException(nameof(sender));

    public async Task<int> DispatchAsync(
        DateTimeOffset nowUtc,
        IReadOnlyCollection<string> allowedChannels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedChannels);
        var channels = allowedChannels
            .Where(channel => channel is "Email" or "MobilePush")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (channels.Length == 0)
        {
            return 0;
        }

        var now = nowUtc.ToUniversalTime();
        var deliveries = await _store.ClaimDueAsync(
            now, LeaseDuration, BatchSize, channels, cancellationToken).ConfigureAwait(false);

        foreach (var delivery in deliveries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DispatchOneAsync(delivery, now, cancellationToken).ConfigureAwait(false);
        }

        return deliveries.Count;
    }

    private async Task DispatchOneAsync(
        SessionReminderDeliveryLease delivery,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        bool authorized;
        try
        {
            authorized = await _subscriptions.CanDispatchReminderAsync(
                delivery.SessionId,
                delivery.AccountId,
                delivery.SessionVersion,
                delivery.ConsentReference,
                delivery.Channel,
                cancellationToken,
                delivery.SubscriptionVersion).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await RetryAsync(delivery, nowUtc, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!authorized || !await _store.IsLeaseCurrentAsync(
                delivery.DeliveryId, delivery.LeaseId, cancellationToken).ConfigureAwait(false))
        {
            await _store.CompleteAsync(
                delivery.DeliveryId, delivery.LeaseId, SessionReminderDeliveryStatus.Suppressed,
                nowUtc, cancellationToken).ConfigureAwait(false);
            return;
        }

        SessionReminderSendStatus result;
        try
        {
            result = await _sender.SendAsync(delivery, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionReminderPreflightException)
        {
            await RetryAsync(delivery, nowUtc, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Once a provider call might have begun, automatic retry risks duplicate delivery.
            result = SessionReminderSendStatus.OutcomeUnknown;
        }

        switch (result)
        {
            case SessionReminderSendStatus.Accepted:
                await _store.CompleteAsync(delivery.DeliveryId, delivery.LeaseId,
                    SessionReminderDeliveryStatus.Submitted, nowUtc, cancellationToken).ConfigureAwait(false);
                break;
            case SessionReminderSendStatus.NoRoute:
                await _store.CompleteAsync(delivery.DeliveryId, delivery.LeaseId,
                    SessionReminderDeliveryStatus.Suppressed, nowUtc, cancellationToken).ConfigureAwait(false);
                break;
            case SessionReminderSendStatus.NotConfigured:
                await RetryAsync(delivery, nowUtc, cancellationToken).ConfigureAwait(false);
                break;
            case SessionReminderSendStatus.OutcomeUnknown:
            default:
                await _store.CompleteAsync(delivery.DeliveryId, delivery.LeaseId,
                    SessionReminderDeliveryStatus.OutcomeUnknown, nowUtc, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private Task<bool> RetryAsync(
        SessionReminderDeliveryLease delivery,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var exponent = Math.Clamp(delivery.AttemptCount - 1, 0, 10);
        var seconds = Math.Min(MaximumBackoffSeconds, 30 * (1 << exponent));
        return _store.RetryAsync(
            delivery.DeliveryId,
            delivery.LeaseId,
            nowUtc.AddSeconds(seconds),
            nowUtc,
            cancellationToken);
    }
}
