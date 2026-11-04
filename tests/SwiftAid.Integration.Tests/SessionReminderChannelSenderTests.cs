using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Notifications;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Notifications;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Infrastructure.Persistence.Sessions;

namespace SwiftAid.Integration.Tests;

public sealed class SessionReminderChannelSenderTests
{
    [Fact]
    public async Task ConsentRevokedDuringDeviceLookupPreventsProviderCall()
    {
        var subscriptions = new SubscriptionStub();
        var devices = new DeviceStub(() => subscriptions.Current = false);
        var push = new PushStub();
        var sender = new SessionReminderChannelSender(subscriptions, new DeliveryStub(), devices, push);
        var lease = new SessionReminderDeliveryLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "actor",
            "MobilePush", 3, 1, "consent", 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));

        var result = await sender.SendAsync(lease, CancellationToken.None);

        Assert.Equal(SessionReminderSendStatus.NoRoute, result);
        Assert.Equal(0, push.Calls);
    }

    private sealed class DeviceStub(Action revoke) : IPushDeviceStore
    {
        private int _calls;
        private readonly PushDeviceForDelivery _device = new(Guid.NewGuid(), PushDevicePlatform.Android, false, "test-token");
        public ValueTask<IReadOnlyList<PushDeviceForDelivery>> GetActiveForRecipientAsync(string recipientActorId, CancellationToken cancellationToken)
        {
            if (++_calls == 2) revoke();
            return ValueTask.FromResult<IReadOnlyList<PushDeviceForDelivery>>([_device]);
        }
        public ValueTask<PushDeviceRegistrationStatus> RegisterAsync(ActorContext actor, PushDeviceRegistrationRequest request, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<PushDeviceRemovalStatus> RevokeAsync(ActorContext actor, Guid installationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<PushDeviceRemovalStatus> RevokeAllAsync(ActorContext actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PushStub : IPushNotificationSender
    {
        public int Calls { get; private set; }
        public ValueTask<PushNotificationResult> SendAsync(PushNotificationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new PushNotificationResult(PushNotificationStatus.AcceptedByProvider));
        }
    }

    private sealed class SubscriptionStub : IEventSubscriptionService
    {
        public bool Current { get; set; } = true;
        public Task<bool> CanDispatchReminderAsync(Guid sessionId, string accountId, long sessionVersion, string consentReference, string channel, CancellationToken cancellationToken, long? expectedSubscriptionVersion = null) =>
            Task.FromResult(Current);
        public Task<EventSubscriptionOptions?> GetOptionsAsync(Guid sessionId, string accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EventSubscription?> GetAsync(Guid sessionId, string accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EventSubscription>> ListMineAsync(string accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EventSubscription> SubscribeAsync(Guid sessionId, string accountId, long expectedSessionVersion, string consentReference, string channel, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EventSubscription> UnsubscribeAsync(Guid sessionId, string accountId, long expectedSubscriptionVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class DeliveryStub : ISessionReminderDeliveryStore
    {
        public Task<bool> IsLeaseCurrentAsync(Guid deliveryId, Guid leaseId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<int> MaterializeDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SessionReminderDeliveryLease>> ClaimDueAsync(DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit, IReadOnlyCollection<string> allowedChannels, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> CompleteAsync(Guid deliveryId, Guid leaseId, SessionReminderDeliveryStatus status, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> RetryAsync(Guid deliveryId, Guid leaseId, DateTimeOffset availableAtUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
