using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Application.Tests;

public sealed class SessionReminderDeliveryDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dispatch_rechecks_exact_subscription_version_before_sending()
    {
        var store = new FakeDeliveryStore(CreateLease(subscriptionVersion: 4));
        var subscriptions = new FakeSubscriptionService { IsAuthorized = false };
        var sender = new FakeSender(SessionReminderSendStatus.Accepted);
        var dispatcher = new SessionReminderDeliveryDispatcher(store, subscriptions, sender);

        Assert.Equal(1, await dispatcher.DispatchAsync(Now, ["Email"], CancellationToken.None));

        Assert.Equal(4, subscriptions.ExpectedSubscriptionVersion);
        Assert.Equal(0, sender.CallCount);
        Assert.Equal(SessionReminderDeliveryStatus.Suppressed, store.CompletedStatus);
        Assert.Null(store.RetryAtUtc);
    }

    [Fact]
    public async Task Not_configured_channel_retries_with_bounded_backoff()
    {
        var store = new FakeDeliveryStore(CreateLease(attemptCount: 3));
        var sender = new FakeSender(SessionReminderSendStatus.NotConfigured);
        var dispatcher = new SessionReminderDeliveryDispatcher(store, new FakeSubscriptionService(), sender);

        await dispatcher.DispatchAsync(Now, ["Email"], CancellationToken.None);

        Assert.Equal(Now.AddSeconds(120), store.RetryAtUtc);
        Assert.Null(store.CompletedStatus);
    }

    [Fact]
    public async Task Ambiguous_provider_outcome_is_terminal_and_not_retried()
    {
        var store = new FakeDeliveryStore(CreateLease());
        var sender = new FakeSender(SessionReminderSendStatus.OutcomeUnknown);
        var dispatcher = new SessionReminderDeliveryDispatcher(store, new FakeSubscriptionService(), sender);

        await dispatcher.DispatchAsync(Now, ["MobilePush"], CancellationToken.None);

        Assert.Equal(SessionReminderDeliveryStatus.OutcomeUnknown, store.CompletedStatus);
        Assert.Null(store.RetryAtUtc);
    }

    [Fact]
    public async Task Sender_exception_is_held_as_unknown_outcome()
    {
        var store = new FakeDeliveryStore(CreateLease());
        var sender = new FakeSender(exception: new InvalidOperationException("synthetic provider failure"));
        var dispatcher = new SessionReminderDeliveryDispatcher(store, new FakeSubscriptionService(), sender);

        await dispatcher.DispatchAsync(Now, ["Email"], CancellationToken.None);

        Assert.Equal(SessionReminderDeliveryStatus.OutcomeUnknown, store.CompletedStatus);
        Assert.Null(store.RetryAtUtc);
    }

    [Fact]
    public async Task Preflight_failure_retries_before_provider_submission()
    {
        var store = new FakeDeliveryStore(CreateLease());
        var sender = new FakeSender(exception: new SessionReminderPreflightException(
            new InvalidOperationException("synthetic store failure")));
        var dispatcher = new SessionReminderDeliveryDispatcher(store, new FakeSubscriptionService(), sender);

        await dispatcher.DispatchAsync(Now, ["Email"], CancellationToken.None);

        Assert.Equal(Now.AddSeconds(30), store.RetryAtUtc);
        Assert.Null(store.CompletedStatus);
    }

    [Fact]
    public async Task Disabled_channels_are_not_claimed()
    {
        var store = new FakeDeliveryStore(CreateLease());
        var dispatcher = new SessionReminderDeliveryDispatcher(
            store, new FakeSubscriptionService(), new FakeSender(SessionReminderSendStatus.Accepted));

        Assert.Equal(0, await dispatcher.DispatchAsync(Now, [], CancellationToken.None));
        Assert.False(store.ClaimCalled);
    }

    private static SessionReminderDeliveryLease CreateLease(int subscriptionVersion = 1, int attemptCount = 1) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "account-1", "Email", 3, subscriptionVersion,
        "session-reminder-consent-v1:abc:3", attemptCount, Guid.NewGuid(), Now.AddMinutes(1));

    private sealed class FakeDeliveryStore(SessionReminderDeliveryLease lease) : ISessionReminderDeliveryStore
    {
        public bool ClaimCalled { get; private set; }
        public SessionReminderDeliveryStatus? CompletedStatus { get; private set; }
        public DateTimeOffset? RetryAtUtc { get; private set; }

        public Task<int> MaterializeDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<SessionReminderDeliveryLease>> ClaimDueAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int limit,
            IReadOnlyCollection<string> allowedChannels,
            CancellationToken cancellationToken)
        {
            ClaimCalled = true;
            return Task.FromResult<IReadOnlyList<SessionReminderDeliveryLease>>([lease]);
        }

        public Task<bool> IsLeaseCurrentAsync(Guid deliveryId, Guid leaseId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> CompleteAsync(
            Guid deliveryId,
            Guid leaseId,
            SessionReminderDeliveryStatus status,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            CompletedStatus = status;
            return Task.FromResult(true);
        }

        public Task<bool> RetryAsync(
            Guid deliveryId,
            Guid leaseId,
            DateTimeOffset availableAtUtc,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            RetryAtUtc = availableAtUtc;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeSubscriptionService : IEventSubscriptionService
    {
        public bool IsAuthorized { get; init; } = true;
        public long? ExpectedSubscriptionVersion { get; private set; }

        public Task<EventSubscriptionOptions?> GetOptionsAsync(Guid sessionId, string accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EventSubscription?> GetAsync(Guid sessionId, string accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EventSubscription>> ListMineAsync(string accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EventSubscription> SubscribeAsync(Guid sessionId, string accountId, long expectedSessionVersion,
            string consentReference, string channel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EventSubscription> UnsubscribeAsync(Guid sessionId, string accountId, long expectedSubscriptionVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CanDispatchReminderAsync(Guid sessionId, string accountId, long sessionVersion,
            string consentReference, string channel, CancellationToken cancellationToken,
            long? expectedSubscriptionVersion = null)
        {
            ExpectedSubscriptionVersion = expectedSubscriptionVersion;
            return Task.FromResult(IsAuthorized);
        }
    }

    private sealed class FakeSender(
        SessionReminderSendStatus? result = null,
        Exception? exception = null) : ISessionReminderChannelSender
    {
        public int CallCount { get; private set; }

        public Task<SessionReminderSendStatus> SendAsync(
            SessionReminderDeliveryLease delivery,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (exception is not null) throw exception;
            return Task.FromResult(result ?? SessionReminderSendStatus.Accepted);
        }
    }
}
