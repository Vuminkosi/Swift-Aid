namespace SwiftAid.Client.Presentation;

/// <summary>Fail-closed map aggregates until an authorized live provider is available.</summary>
public sealed class UnavailableMapAggregateClient : IMapAggregateClient
{
    public Task<UiCapabilityResult<MapAggregatePage>> GetAsync(
        MapAggregateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!query.IsValid)
        {
            return Task.FromResult(new UiCapabilityResult<MapAggregatePage>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                UiCapabilityResults.UnavailableMetadata("map-aggregate", "Choose a supported period or region."),
                "Choose a supported period or region."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<MapAggregatePage>("map-aggregate"));
    }
}
