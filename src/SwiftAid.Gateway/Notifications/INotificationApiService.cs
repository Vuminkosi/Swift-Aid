using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Gateway.Notifications;

/// <summary>
/// The transport-facing notification operations. Implementations must scope every operation to
/// the supplied server-derived actor and re-check current resource authority before returning an
/// item or applying a state change.
/// </summary>
public interface INotificationApiService
{
    ValueTask<NotificationFeedPage> GetAsync(
        ActorContext actor,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);

    ValueTask<NotificationMutationStatus> MarkReadAsync(
        ActorContext actor,
        Guid notificationId,
        CancellationToken cancellationToken);

    ValueTask<NotificationMutationStatus> DismissAsync(
        ActorContext actor,
        Guid notificationId,
        CancellationToken cancellationToken);

    ValueTask<PushDeviceRegistrationStatus> RegisterDeviceAsync(
        ActorContext actor,
        PushDeviceRegistrationRequest request,
        CancellationToken cancellationToken);

    ValueTask<PushDeviceRemovalStatus> RemoveDeviceAsync(
        ActorContext actor,
        Guid installationId,
        CancellationToken cancellationToken);
}

/// <summary>Minimal authorized feed page. Counts and continuation tokens are calculated after authorization filtering.</summary>
public sealed record NotificationFeedPage(
    IReadOnlyList<NotificationItemView> Items,
    string? ContinuationToken,
    int TotalCount,
    int UnreadCount);

public enum NotificationMutationStatus
{
    Applied,
    NotFoundOrNotAuthorized,
    Unavailable
}

