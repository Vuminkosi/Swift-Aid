using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Ui.TestHost;

public sealed class SurveillanceLoadingFixture : IMapAggregateClient, ISurveillanceMapDetailsClient
{
    private readonly TaskCompletionSource<UiCapabilityResult<SurveillanceMapDetails>> _details = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly UiCapabilityMetadata Metadata = UiCapabilityResults.SyntheticMetadata("map", "Fixture");

    public Task<UiCapabilityResult<MapAggregatePage>> GetAsync(MapAggregateQuery query, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var area = new MapAreaAggregate("SZ", "Synthetic region", MapAggregateAreaPrecision.Country, 5, 5, 10, false, null, now);
        var page = new MapAggregatePage(new Page<MapAreaAggregate>([area], 1, 100, 1, false), now);
        return Task.FromResult(new UiCapabilityResult<MapAggregatePage>(UiCapabilityResultCode.Success, page, Metadata));
    }

    public Task<UiCapabilityResult<SurveillanceMapDetails>> GetAsync(string countryCode, MapAggregatePeriod period, CancellationToken ct = default) => _details.Task.WaitAsync(ct);
    public void FailDetails() => _details.TrySetException(new HttpRequestException("Synthetic failure"));
    public void DenyDetails() => _details.TrySetResult(new(UiCapabilityResultCode.Denied, null, Metadata));
}
