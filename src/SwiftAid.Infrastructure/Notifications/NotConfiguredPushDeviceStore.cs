using SwiftAid.Application.Authorization;
using SwiftAid.Application.Notifications;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Infrastructure.Notifications;

/// <summary>
/// Fail-closed device store for hosts without identity or data protection.
/// Never persists or returns token material; every operation reports unavailable.
/// </summary>
public sealed class NotConfiguredPushDeviceStore : IPushDeviceStore
{
    public ValueTask<PushDeviceRegistrationStatus> RegisterAsync(
        ActorContext actor,
        PushDeviceRegistrationRequest request,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(PushDeviceRegistrationStatus.Unavailable);

    public ValueTask<PushDeviceRemovalStatus> RevokeAsync(
        ActorContext actor,
        Guid installationId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(PushDeviceRemovalStatus.Unavailable);

    public ValueTask<PushDeviceRemovalStatus> RevokeAllAsync(
        ActorContext actor,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(PushDeviceRemovalStatus.Unavailable);

    public ValueTask<IReadOnlyList<PushDeviceForDelivery>> GetActiveForRecipientAsync(
        string recipientActorId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PushDeviceForDelivery>>(Array.Empty<PushDeviceForDelivery>());
}
