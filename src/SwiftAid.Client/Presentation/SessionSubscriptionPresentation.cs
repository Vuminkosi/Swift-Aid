namespace SwiftAid.Client.Presentation;

/// <summary>Normalizes authentication and subscription state for reminder presentation.</summary>
public static class SessionSubscriptionPresentation
{
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
