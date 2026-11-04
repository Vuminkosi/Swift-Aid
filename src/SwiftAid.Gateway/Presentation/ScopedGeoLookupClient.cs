using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Overlapping autocomplete requests must not share a circuit's DbContext.</summary>
public sealed class ScopedGeoLookupClient(IServiceScopeFactory scopes) : IGeoLookupClient
{
    public async Task<UiCapabilityResult<IReadOnlyList<GeoLookupOption>>> SearchAsync(
        string query, string? countryCode, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await new GatewayGeoLookupClient(scope.ServiceProvider.GetRequiredService<IGeographicRegistry>())
            .SearchAsync(query, countryCode, cancellationToken);
    }

    public async Task<UiCapabilityResult<IReadOnlyList<GeoLookupOption>>> GetChildrenAsync(
        string parentId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await new GatewayGeoLookupClient(scope.ServiceProvider.GetRequiredService<IGeographicRegistry>())
            .GetChildrenAsync(parentId, cancellationToken);
    }
}
