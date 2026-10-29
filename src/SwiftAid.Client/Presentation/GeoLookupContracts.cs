using SwiftAid.Contracts.Geography;

namespace SwiftAid.Client.Presentation;

public sealed record GeoLookupAncestor(string Id, string Name, GeoLocationKind Kind);

public sealed record GeoLookupOption(
    string Id,
    string Name,
    GeoLocationKind Kind,
    string CountryCode,
    string CountryName,
    string Ancestry,
    bool HasChildren,
    IReadOnlyList<GeoLookupAncestor> Ancestors);

public sealed record GeoLookupCountry(string Code, string Name);

/// <summary>Registry-driven location lookup for search-first pickers and cascading selectors.</summary>
public interface IGeoLookupClient
{
    Task<UiCapabilityResult<IReadOnlyList<GeoLookupOption>>> SearchAsync(
        string query,
        string? countryCode,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<IReadOnlyList<GeoLookupOption>>> GetChildrenAsync(
        string parentId,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableGeoLookupClient : IGeoLookupClient
{
    public Task<UiCapabilityResult<IReadOnlyList<GeoLookupOption>>> SearchAsync(
        string query,
        string? countryCode,
        CancellationToken cancellationToken = default) =>
        Unavailable<IReadOnlyList<GeoLookupOption>>(cancellationToken);

    public Task<UiCapabilityResult<IReadOnlyList<GeoLookupOption>>> GetChildrenAsync(
        string parentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        return Unavailable<IReadOnlyList<GeoLookupOption>>(cancellationToken);
    }

    private static Task<UiCapabilityResult<T>> Unavailable<T>(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<T>("geo-lookup"));
    }
}
