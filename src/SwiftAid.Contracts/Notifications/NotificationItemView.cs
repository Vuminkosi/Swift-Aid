using System.Text.Json.Serialization;

namespace SwiftAid.Contracts.Notifications;

/// <summary>
/// Minimal recipient-authorized notification projection. The display text is derived only from
/// a closed kind; no subject, condition, location, or media details are carried on the wire.
/// </summary>
public sealed record NotificationItemView(
    Guid Id,
    NotificationKind Kind,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc,
    DateTimeOffset? DismissedAtUtc,
    NotificationAction? Action,
    string? OpaqueResourceReference)
{
    public string DisplayText => Kind switch
    {
        NotificationKind.CaseNeedsAttention => "A case needs your attention.",
        _ => "You have an update to review."
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<NotificationKind>))]
public enum NotificationKind
{
    CaseNeedsAttention
}

[JsonConverter(typeof(JsonStringEnumConverter<NotificationAction>))]
public enum NotificationAction
{
    OpenAssignedWork
}

[JsonConverter(typeof(JsonStringEnumConverter<PushDevicePlatform>))]
public enum PushDevicePlatform
{
    Android,
    Ios,
    Web
}

/// <summary>Authenticated device request. The token is accepted only on registration requests.</summary>
public sealed record PushDeviceRegistrationRequest(
    Guid InstallationId,
    PushDevicePlatform Platform,
    string Token,
    bool SoundEnabled = false);

[JsonConverter(typeof(JsonStringEnumConverter<PushDeviceRegistrationStatus>))]
public enum PushDeviceRegistrationStatus
{
    Registered,
    Invalid,
    NotAuthenticated,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<PushDeviceRemovalStatus>))]
public enum PushDeviceRemovalStatus
{
    Removed,
    NotFoundOrNotAuthorized,
    NotAuthenticated,
    Unavailable
}

/// <summary>Safe registration result; never contains the provider token.</summary>
public sealed record PushDeviceRegistrationResult(PushDeviceRegistrationStatus Status);

/// <summary>Safe removal result; never contains the provider token.</summary>
public sealed record PushDeviceRemovalResult(PushDeviceRemovalStatus Status);
