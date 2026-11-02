namespace SwiftAid.Domain.Notifications;

/// <summary>
/// A recipient-scoped attention occurrence. It contains only a closed notification kind and an
/// opaque resource reference; display text is derived from the kind and is never stored here.
/// </summary>
public sealed class NotificationItem
{
    private NotificationItem()
    {
        OccurrenceKey = string.Empty;
        RecipientActorId = string.Empty;
    }

    private NotificationItem(
        Guid notificationId,
        Guid acceptedEventId,
        string occurrenceKey,
        string recipientActorId,
        NotificationKind kind,
        NotificationResourceKind resourceKind,
        Guid resourceId,
        Guid? relatedResourceId,
        long? relatedResourceVersion,
        NotificationAction action,
        NotificationRecipientRequirement recipientRequirement,
        DateTimeOffset createdAtUtc)
    {
        NotificationId = notificationId;
        AcceptedEventId = acceptedEventId;
        OccurrenceKey = occurrenceKey;
        RecipientActorId = recipientActorId;
        Kind = kind;
        ResourceKind = resourceKind;
        ResourceId = resourceId;
        RelatedResourceId = relatedResourceId;
        RelatedResourceVersion = relatedResourceVersion;
        Action = action;
        RecipientRequirement = recipientRequirement;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid NotificationId { get; private set; }

    /// <summary>The accepted domain event that created this occurrence; distinct from its own ID.</summary>
    public Guid AcceptedEventId { get; private set; }

    /// <summary>A trusted, stable, non-user-supplied key for one recipient occurrence.</summary>
    public string OccurrenceKey { get; private set; }

    public string RecipientActorId { get; private set; }

    public NotificationKind Kind { get; private set; }

    public NotificationResourceKind ResourceKind { get; private set; }

    /// <summary>An opaque resource identifier; it grants no access by itself.</summary>
    public Guid ResourceId { get; private set; }

    /// <summary>Opaque child resource used to recheck a scheduled action, never exposed to clients.</summary>
    public Guid? RelatedResourceId { get; private set; }

    /// <summary>Version of the related scheduled action that created this occurrence.</summary>
    public long? RelatedResourceVersion { get; private set; }

    public NotificationAction Action { get; private set; }

    /// <summary>Internal recipient constraint, never included in a client projection.</summary>
    public NotificationRecipientRequirement RecipientRequirement { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public DateTimeOffset? DismissedAtUtc { get; private set; }

    public static NotificationItem Create(
        Guid acceptedEventId,
        string occurrenceKey,
        string recipientActorId,
        NotificationKind kind,
        NotificationResourceKind resourceKind,
        Guid resourceId,
        NotificationAction action,
        NotificationRecipientRequirement recipientRequirement,
        DateTimeOffset createdAtUtc,
        Guid? relatedResourceId = null,
        long? relatedResourceVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrenceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientActorId);

        if (occurrenceKey.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrenceKey));
        }

        if (recipientActorId.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(recipientActorId));
        }

        if (acceptedEventId == Guid.Empty ||
            !Enum.IsDefined(kind) ||
            !Enum.IsDefined(resourceKind) ||
            !Enum.IsDefined(action) ||
            !Enum.IsDefined(recipientRequirement) ||
            resourceId == Guid.Empty ||
            (relatedResourceId is Guid relatedId && relatedId == Guid.Empty) ||
            ((relatedResourceId is null) != (relatedResourceVersion is null)) ||
            relatedResourceVersion is <= 0 ||
            (recipientRequirement == NotificationRecipientRequirement.CurrentFollowUpAssignee &&
             (relatedResourceId is null || relatedResourceVersion is null)) ||
            (recipientRequirement != NotificationRecipientRequirement.CurrentFollowUpAssignee &&
             relatedResourceId is not null))
        {
            throw new ArgumentException("The notification kind, resource, and action must be valid.");
        }

        return new NotificationItem(
            Guid.NewGuid(),
            acceptedEventId,
            occurrenceKey,
            recipientActorId,
            kind,
            resourceKind,
            resourceId,
            relatedResourceId,
            relatedResourceVersion,
            action,
            recipientRequirement,
            createdAtUtc.ToUniversalTime());
    }

    public void MarkRead(DateTimeOffset readAtUtc)
    {
        ReadAtUtc ??= readAtUtc.ToUniversalTime();
    }

    public void Dismiss(DateTimeOffset dismissedAtUtc)
    {
        DismissedAtUtc ??= dismissedAtUtc.ToUniversalTime();
    }
}

public enum NotificationKind
{
    CaseNeedsAttention = 1
}

public enum NotificationResourceKind
{
    ManagedCase = 1
}

public enum NotificationAction
{
    OpenCase = 1
}

/// <summary>Trusted server rule for current resource access; never client-selectable.</summary>
public enum NotificationRecipientRequirement
{
    CurrentAccountableOwner = 1,
    AuthorizedCaseMember = 2,
    CurrentFollowUpAssignee = 3
}
