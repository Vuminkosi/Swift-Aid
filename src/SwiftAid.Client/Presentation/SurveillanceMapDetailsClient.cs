using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Client.Presentation;

public interface ISurveillanceMapDetailsClient
{
    Task<UiCapabilityResult<SurveillanceMapDetails>> GetAsync(string countryCode, MapAggregatePeriod period,
        CancellationToken ct = default);
}
