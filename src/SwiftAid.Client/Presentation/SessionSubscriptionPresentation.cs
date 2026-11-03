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
}
