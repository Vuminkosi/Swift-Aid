using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Application.Tests;

public sealed class EventSubscriptionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SessionId = Guid.Parse("a3310000-3333-4333-8333-333333333333");
    private const string AccountId = "member-17";

    [Fact]
    public async Task Consent_options_bind_to_current_published_session_version()
    {
        var (service, sessions, _) = Create();

        var options = Assert.IsType<EventSubscriptionOptions>(await service.GetOptionsAsync(SessionId, AccountId, default));
        Assert.Equal(4, options.SessionVersion);
        Assert.Contains("Email", options.AvailableChannels);
        Assert.Contains("MobilePush", options.AvailableChannels);
        Assert.True(options.IsCurrent);

        sessions.PublicSession = PublicSession(version: 5);
        await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => service.SubscribeAsync(SessionId,
            AccountId, options.SessionVersion, options.ConsentReference, "Email", default));
    }

    [Fact]
    public async Task Subscribe_is_idempotent_and_dispatch_requires_current_consent()
    {
        var (service, _, store) = Create();
        var options = Assert.IsType<EventSubscriptionOptions>(await service.GetOptionsAsync(SessionId, AccountId, default));

        var first = await service.SubscribeAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default);
        var duplicate = await service.SubscribeAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default);

        Assert.Equal(EventSubscriptionStatus.Active, first.Status);
        Assert.Equal(first.Version, duplicate.Version);
        Assert.True(await service.CanDispatchReminderAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default, first.Version));
        Assert.False(await service.CanDispatchReminderAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default, first.Version + 1));
        Assert.False(await service.CanDispatchReminderAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "MobilePush", default));

        var removed = await service.UnsubscribeAsync(SessionId, AccountId, first.Version, default);
        Assert.Equal(EventSubscriptionStatus.Unsubscribed, removed.Status);
        Assert.False(await service.CanDispatchReminderAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default));
        Assert.Equal(EventSubscriptionStatus.Unsubscribed,
            (await store.GetAsync(SessionId, AccountId, default))!.Status);
        await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => service.SubscribeAsync(SessionId, AccountId,
            options.SessionVersion, options.ConsentReference, "Email", default));
        var freshOptions = Assert.IsType<EventSubscriptionOptions>(await service.GetOptionsAsync(SessionId, AccountId, default));
        var renewed = await service.SubscribeAsync(SessionId, AccountId, freshOptions.SessionVersion,
            freshOptions.ConsentReference, "Email", default);
        Assert.Equal(EventSubscriptionStatus.Active, renewed.Status);
    }

    [Fact]
    public async Task ChangingChannelRequiresFreshConsentAndStopsTheOldChannel()
    {
        var (service, _, _) = Create();
        var firstOptions = Assert.IsType<EventSubscriptionOptions>(await service.GetOptionsAsync(SessionId, AccountId, default));
        var first = await service.SubscribeAsync(SessionId, AccountId, firstOptions.SessionVersion,
            firstOptions.ConsentReference, "Email", default);
        var nextOptions = Assert.IsType<EventSubscriptionOptions>(await service.GetOptionsAsync(SessionId, AccountId, default));

        await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => service.SubscribeAsync(SessionId, AccountId,
            firstOptions.SessionVersion, firstOptions.ConsentReference, "MobilePush", default));
        var changed = await service.SubscribeAsync(SessionId, AccountId, nextOptions.SessionVersion,
            nextOptions.ConsentReference, "MobilePush", default);

        Assert.True(changed.Version > first.Version);
        Assert.False(await service.CanDispatchReminderAsync(SessionId, AccountId, firstOptions.SessionVersion,
            firstOptions.ConsentReference, "Email", default, first.Version));
        Assert.True(await service.CanDispatchReminderAsync(SessionId, AccountId, nextOptions.SessionVersion,
            nextOptions.ConsentReference, "MobilePush", default, changed.Version));
    }

    [Fact]
    public async Task Unpublished_or_started_sessions_cannot_offer_or_dispatch_reminders()
    {
        var (service, sessions, _) = Create();
        sessions.PublicSession = PublicSession(status: "Cancelled");
        Assert.Null(await service.GetOptionsAsync(SessionId, AccountId, default));

        sessions.PublicSession = PublicSession(start: Now.AddSeconds(-1));
        Assert.Null(await service.GetOptionsAsync(SessionId, AccountId, default));
        Assert.False(await service.CanDispatchReminderAsync(SessionId, AccountId, 4,
            "session-reminder-consent-v1:a3310000333343338333333333333333:4", "Email", default));
    }

    [Fact]
    public async Task Suspended_or_missing_account_cannot_receive_reminder_dispatch()
    {
        var (service, _, store) = Create();
        var options = Assert.IsType<EventSubscriptionOptions>(await service.GetOptionsAsync(SessionId, AccountId, default));
        await service.SubscribeAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default);

        store.AccountActive = false;

        Assert.False(await service.CanDispatchReminderAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default));
    }

    [Fact]
    public async Task ExistingLegacyConsentRemainsValidButCannotBeReplayedAsANewSubscribe()
    {
        var (service, _, store) = Create();
        var legacy = $"session-reminder-consent-v1:{SessionId:N}:4";
        await store.SubscribeAsync(SessionId, AccountId, 4, 0, "Email", legacy, Now, default);

        Assert.Equal(EventSubscriptionStatus.Active, (await service.GetAsync(SessionId, AccountId, default))!.Status);
        Assert.True(await service.CanDispatchReminderAsync(SessionId, AccountId, 4, legacy, "Email", default));
        await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => service.SubscribeAsync(SessionId, AccountId,
            4, legacy, "Email", default));
    }

    [Fact]
    public async Task Own_subscription_list_masks_stale_session_consent()
    {
        var (service, sessions, store) = Create();
        var options = Assert.IsType<EventSubscriptionOptions>(await service.GetOptionsAsync(SessionId, AccountId, default));
        await service.SubscribeAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default);
        sessions.PublicSession = PublicSession(version: options.SessionVersion + 1);

        var items = await service.ListMineAsync(AccountId, default);

        var item = Assert.Single(items);
        Assert.Equal(SessionId, item.SessionId);
        Assert.Equal(EventSubscriptionStatus.Unsubscribed, item.Status);
        Assert.Empty(item.Channels);
        Assert.Null(item.ConsentReference);
        Assert.True(item.HasInactiveStoredConsent);
        Assert.False(await service.CanDispatchReminderAsync(SessionId, AccountId, options.SessionVersion,
            options.ConsentReference, "Email", default));
        await service.UnsubscribeAsync(SessionId, AccountId, item.Version, default);
        Assert.Equal(EventSubscriptionStatus.Unsubscribed, (await store.GetAsync(SessionId, AccountId, default))!.Status);
        Assert.False(Assert.Single(await service.ListMineAsync(AccountId, default)).HasInactiveStoredConsent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ineligible_or_unavailable_session_keeps_saved_consent_withdrawable(bool unavailable)
    {
        var (service, sessions, store) = Create();
        var options = Assert.IsType<EventSubscriptionOptions>(await service.GetOptionsAsync(SessionId, AccountId, default));
        await service.SubscribeAsync(SessionId, AccountId, options.SessionVersion, options.ConsentReference, "Email", default);
        sessions.PublicSession = unavailable ? null : PublicSession(status: "Cancelled");

        var item = Assert.Single(await service.ListMineAsync(AccountId, default));
        Assert.True(item.HasInactiveStoredConsent);
        Assert.Empty(item.Channels);
        await service.UnsubscribeAsync(SessionId, AccountId, item.Version, default);
        Assert.Equal(EventSubscriptionStatus.Unsubscribed, (await store.GetAsync(SessionId, AccountId, default))!.Status);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" member-17")]
    [InlineData("member-17\n")]
    public async Task Account_identity_must_be_canonical(string accountId)
    {
        var (service, _, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(SessionId, accountId, default));
    }

    private static (EventSubscriptionService Service, FakeCommunitySessionService Sessions, FakeSubscriptionStore Store) Create()
    {
        var sessions = new FakeCommunitySessionService(PublicSession());
        var store = new FakeSubscriptionStore();
        return (new EventSubscriptionService(store, sessions, new FixedTimeProvider(Now)), sessions, store);
    }

    private static PublicSessionView PublicSession(long version = 4, string status = "Scheduled", DateTimeOffset? start = null) =>
        new(SessionId, version, "Community session", "Summary", "Public audience", [], "SZ", "Hhohho", "HumanHealth",
            "Presenter", "en", start ?? Now.AddDays(2), "Africa/Mbabane", 60, null, [], SessionChannelType.None, null,
            status, null, version);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSubscriptionStore : IEventSubscriptionStore
    {
        private readonly Dictionary<(Guid SessionId, string AccountId), EventSubscription> _subscriptions = [];

        public bool AccountActive { get; set; } = true;

        public Task<bool> IsAccountActiveAsync(string accountId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(AccountActive);

        public Task<EventSubscription?> GetAsync(Guid sessionId, string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(_subscriptions.GetValueOrDefault((sessionId, accountId)));

        public Task<IReadOnlyList<EventSubscription>> ListByAccountAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventSubscription>>(_subscriptions
                .Where(item => item.Key.AccountId == accountId)
                .Select(item => item.Value)
                .OrderBy(item => item.SessionId)
                .ToArray());

        public Task<EventSubscription> SubscribeAsync(Guid sessionId, string accountId, long expectedSessionVersion, long expectedSubscriptionVersion, string channel,
            string consentReference, DateTimeOffset recordedAtUtc, CancellationToken cancellationToken)
        {
            var key = (sessionId, accountId);
            if (_subscriptions.TryGetValue(key, out var existing) && existing.Status == EventSubscriptionStatus.Active &&
                existing.Channels.SequenceEqual([channel], StringComparer.Ordinal) && existing.ConsentReference == consentReference)
                return Task.FromResult(existing);
            if ((existing?.Version ?? 0) != expectedSubscriptionVersion)
                throw new SessionSubscriptionConflictException(sessionId);
            var next = new EventSubscription(sessionId, accountId, EventSubscriptionStatus.Active, [channel], consentReference,
                existing?.Version + 1 ?? 1);
            _subscriptions[key] = next;
            return Task.FromResult(next);
        }

        public Task<EventSubscription> UnsubscribeAsync(Guid sessionId, string accountId, long expectedVersion,
            DateTimeOffset recordedAtUtc, CancellationToken cancellationToken)
        {
            var key = (sessionId, accountId);
            if (!_subscriptions.TryGetValue(key, out var existing))
                return Task.FromResult(new EventSubscription(sessionId, accountId, EventSubscriptionStatus.Unsubscribed, [], null, 0));
            if (existing.Status == EventSubscriptionStatus.Unsubscribed) return Task.FromResult(existing);
            if (existing.Version != expectedVersion) throw new SessionSubscriptionConflictException(sessionId);
            var next = existing with { Status = EventSubscriptionStatus.Unsubscribed, Channels = [], ConsentReference = null, Version = existing.Version + 1 };
            _subscriptions[key] = next;
            return Task.FromResult(next);
        }
    }

    private sealed class FakeCommunitySessionService(PublicSessionView? publicSession) : ICommunitySessionService
    {
        public PublicSessionView? PublicSession { get; set; } = publicSession;
        public Task<PublicSessionView?> GetPublicAsync(Guid sessionId, CancellationToken ct) =>
            Task.FromResult(PublicSession?.SessionId == sessionId ? PublicSession : null);
        public Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(int? limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PublicSessionView>>(PublicSession is null ? [] : [PublicSession]);
        public Task<PublicSessionPage> QueryPublicAsync(PublicSessionQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SessionCommandResult> ExecuteAsync(SessionCommand command, string actorId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<CommunitySessionView?> GetStaffAsync(Guid sessionId, string actorId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string?> GetIcsAsync(Guid sessionId, DateTimeOffset generatedAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string?> GetInvitationTextAsync(Guid sessionId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
