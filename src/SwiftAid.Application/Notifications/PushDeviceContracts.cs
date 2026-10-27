using System.Text.Json.Serialization;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Application.Notifications;

/// <summary>Provider credential available only to trusted server-side dispatch code.</summary>
public sealed record PushDeviceForDelivery(
    Guid InstallationId,
    PushDevicePlatform Platform,
    bool SoundEnabled,
    [property: JsonIgnore] string Token);

/// <summary>
/// Authenticated actor-scoped device registration and revocation. Tokens are protected at rest;
/// token material is never returned by a registration or removal operation.
/// </summary>
public interface IPushDeviceStore
{
    ValueTask<PushDeviceRegistrationStatus> RegisterAsync(
        ActorContext actor,
        PushDeviceRegistrationRequest request,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    ValueTask<PushDeviceRemovalStatus> RevokeAsync(
        ActorContext actor,
        Guid installationId,
        CancellationToken cancellationToken);

    /// <summary>Revokes all stored tokens for an authenticated logout or account shutdown.</summary>
    ValueTask<PushDeviceRemovalStatus> RevokeAllAsync(
        ActorContext actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Internal dispatch lookup by recipient derived from a trusted notification/outbox record.
    /// Implementations decrypt tokens in memory only; callers must not log or serialize them.
    /// </summary>
    ValueTask<IReadOnlyList<PushDeviceForDelivery>> GetActiveForRecipientAsync(
        string recipientActorId,
        CancellationToken cancellationToken);
}
