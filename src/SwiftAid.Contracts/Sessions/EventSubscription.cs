namespace SwiftAid.Contracts.Sessions;

public enum EventSubscriptionStatus
{
    Active,
    Unsubscribed
}

/// <summary>Member's private session subscription; consent is distinct from login or attendance.</summary>
public sealed record EventSubscription(
    Guid SessionId,
    string AccountId,
    EventSubscriptionStatus Status,
    IReadOnlyList<string> Channels,
    string? ConsentReference,
    long Version);

/// <summary>Current version and explicit consent choices for a public scheduled session.</summary>
public sealed record EventSubscriptionOptions(
    Guid SessionId,
    long SessionVersion,
    string ConsentReference,
    string ConsentText,
    IReadOnlyList<string> AvailableChannels,
    bool IsCurrent);
