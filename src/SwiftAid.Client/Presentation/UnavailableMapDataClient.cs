namespace SwiftAid.Client.Presentation;

/// <summary>Fail-closed detailed observation map capability.</summary>
public sealed class UnavailableMapDataClient : IMapDataClient
{
    public Task<UiCapabilityResult<MapDataPage>> GetAsync(
        MapDataQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<MapDataPage>("map-observations"));
    }
}
