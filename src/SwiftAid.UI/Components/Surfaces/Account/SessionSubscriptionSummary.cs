namespace SwiftAid.UI.Components.Surfaces.Account;

/// <summary>Display-safe details for one subscription owned by the signed-in account.</summary>
public sealed record SessionSubscriptionSummary(
    Guid SessionId,
    long Version,
    string? Title,
    DateTimeOffset? StartsAtUtc,
    string? TimeZoneId,
    string? ChannelLabel,
    bool HasInactiveStoredConsent = false);
