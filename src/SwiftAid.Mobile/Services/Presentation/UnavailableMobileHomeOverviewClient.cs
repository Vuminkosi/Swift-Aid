using SwiftAid.Client.Presentation;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Fails closed until an authorized home overview provider is configured.</summary>
public sealed class UnavailableMobileHomeOverviewClient : IMobileHomeOverviewClient
{
    private const string Capability = "mobile-home-overview";
    private const string UserMessage = "Your work summary is temporarily unavailable.";

    public Task<UiCapabilityResult<MobileHomeOverview>> GetAsync(
        MobileHomeOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (!query.IsValid)
        {
            return Task.FromResult(new UiCapabilityResult<MobileHomeOverview>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                UiCapabilityResults.UnavailableMetadata(Capability, "The requested home summary is unavailable."),
                "The requested home summary is unavailable."));
        }

        return Task.FromResult(new UiCapabilityResult<MobileHomeOverview>(
            UiCapabilityResultCode.Unavailable,
            null,
            UiCapabilityResults.UnavailableMetadata(Capability, UserMessage),
            UserMessage));
    }
}
