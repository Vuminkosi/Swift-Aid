using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Client.Notifications;

public enum NotificationFeedStatus
{
    Ready,
    PermissionDenied,
    Unavailable
}

public sealed record NotificationFeedResult(
    NotificationFeedStatus Status,
    IReadOnlyList<NotificationItemView> Items)
{
    public static NotificationFeedResult Ready(IReadOnlyList<NotificationItemView> items) =>
        new(NotificationFeedStatus.Ready, items);

    public static NotificationFeedResult PermissionDenied() =>
        new(NotificationFeedStatus.PermissionDenied, Array.Empty<NotificationItemView>());

    public static NotificationFeedResult Unavailable() =>
        new(NotificationFeedStatus.Unavailable, Array.Empty<NotificationItemView>());
}
