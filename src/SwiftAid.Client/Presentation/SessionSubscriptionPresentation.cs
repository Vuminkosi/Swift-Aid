namespace SwiftAid.Client.Presentation;

public sealed record SessionSubscriptionUpdateFeedback(bool IsError, string? Message);

/// <summary>Normalizes authentication and subscription state for reminder presentation.</summary>
public static class SessionSubscriptionPresentation
{
    public static bool IsMatchingRead(UiCapabilityResult<SubscriptionView> result, Guid sessionId) =>
        sessionId != Guid.Empty && result.Succeeded && result.Metadata.IsAvailable &&
        result.Metadata.Provenance == UiCapabilityProvenance.LiveService &&
        result.Value is { IsSynthetic: false, Version: >= 0 } value && value.SessionId == sessionId &&
        Enum.IsDefined(value.Status) && (value.Status != SubscriptionStatus.Active || value.Version > 0);

    public static bool IsMatchingOptions(UiCapabilityResult<SubscriptionOptions> result, Guid sessionId) =>
        sessionId != Guid.Empty && result.Succeeded && result.Metadata.IsAvailable &&
        result.Metadata.Provenance == UiCapabilityProvenance.LiveService &&
        result.Value is { Version: > 0, Channels: not null } value && value.SessionId == sessionId &&
        !string.IsNullOrWhiteSpace(value.ConsentReference) && !string.IsNullOrWhiteSpace(value.ConsentText) &&
        value.Channels.All(Enum.IsDefined);

    public static bool CanSubscribe(UiCapabilityResult<SubscriptionOptions> result, Guid sessionId,
        string? consentReference, long expectedVersion, SubscriptionChannel? channel) =>
        IsMatchingOptions(result, sessionId) && result.Value is { IsCurrent: true } options &&
        expectedVersion > 0 && options.Version == expectedVersion &&
        !string.IsNullOrWhiteSpace(consentReference) &&
        string.Equals(options.ConsentReference, consentReference, StringComparison.Ordinal) &&
        channel is { } selected && Enum.IsDefined(selected) && options.Channels.Contains(selected);

    // Subscribe carries a session version; unsubscribe carries a subscription version.
    public static bool IsConfirmedUpdate(UiCapabilityResult<SubscriptionView> result, Guid sessionId, SubscriptionStatus expectedStatus, long expectedVersion) =>
        sessionId != Guid.Empty && expectedVersion >= 0 && (expectedStatus is SubscriptionStatus.Active or SubscriptionStatus.Unsubscribed) &&
        result.Succeeded && result.Metadata.IsAvailable &&
        result.Metadata.Provenance == UiCapabilityProvenance.LiveService &&
        result.Value is { IsSynthetic: false, Version: > 0 } value &&
        value.SessionId == sessionId && value.Status == expectedStatus &&
        (expectedStatus == SubscriptionStatus.Active || value.Version >= expectedVersion);

    public static SessionSubscriptionUpdateFeedback UpdateFeedback(bool confirmed, bool refreshAvailable, string? successMessage, string? refreshMessage)
    {
        if (!confirmed)
            return new(true, "The reminder change could not be confirmed. Refresh and check the current status.");
        if (!refreshAvailable)
            return new(true, "The reminder change was saved, but its current status could not be verified. Refresh and check again.");
        return new(false, successMessage ?? refreshMessage);
    }

    public static SubscriptionStatus? Status(UiCapabilityResult<SubscriptionView> result) => result.Code switch
    {
        UiCapabilityResultCode.Denied => SubscriptionStatus.RequiresAuthentication,
        UiCapabilityResultCode.Success => result.Value?.Status,
        _ => null
    };

    public static string UnsubscribeMessage(UiCapabilityResult<SubscriptionView> result, Guid sessionId)
    {
        if (Status(result) == SubscriptionStatus.RequiresAuthentication)
            return "Sign in to manage your session subscriptions.";
        if (result.Succeeded && result.Value is { Status: SubscriptionStatus.Unsubscribed } value &&
            value.SessionId == sessionId)
            return "You are unsubscribed from this session.";
        if (!result.Succeeded)
            return result.Message ?? result.Metadata.Notice ?? "We could not update this subscription. Refresh and try again.";
        return "We could not confirm the update. Refresh and try again.";
    }
}
