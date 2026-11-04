using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Infrastructure.Intake.Geography;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Isolates initialized registry reads while keeping first-use seeding attached.</summary>
internal sealed class EllaiIsolatedGeographicRegistry(
    IServiceProvider services, EllaiIsolatedToolCalls calls, IGeographicRegistry fallback) : IGeographicRegistry
{
    public async Task<GeoSearchResponse> SearchAsync(string query, string? countryCode, int limit, CancellationToken ct)
    {
        var result = await calls.RunAsync(async token =>
        {
            await using var scope = services.CreateAsyncScope();
            if (scope.ServiceProvider.GetRequiredService<IGeographicRegistry>() is not GeographicRegistryService registry)
                throw new InvalidOperationException("The initialized geographic registry is unavailable.");
            token.ThrowIfCancellationRequested();
            return await registry.SearchIfInitializedAsync(query, countryCode, limit, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return result ?? await fallback.SearchAsync(query, countryCode, limit, ct).ConfigureAwait(false);
    }

    public Task<GeoChildrenResponse> GetChildrenAsync(string parentId, CancellationToken ct) =>
        fallback.GetChildrenAsync(parentId, ct);
}
