using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Serves registry location lookup through the server-side session. No sign-in required.</summary>
public sealed class GatewayGeoLookupClient(IGeographicRegistry registry) : IGeoLookupClient
{
    private const string Capability = "geo-lookup";

    private readonly IGeographicRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task<UiCapabilityResult<IReadOnlyList<GeoLookupOption>>> SearchAsync(
        string query,
        string? countryCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return Invalid<IReadOnlyList<GeoLookupOption>>("Type at least two characters to search places.");
        }

        try
        {
            var response = await _registry.SearchAsync(query.Trim(), countryCode, 25, cancellationToken)
                .ConfigureAwait(false);
            return Success<IReadOnlyList<GeoLookupOption>>(
                response.Results.Select(ToOption).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable<IReadOnlyList<GeoLookupOption>>("Place search is temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<IReadOnlyList<GeoLookupOption>>> GetChildrenAsync(
        string parentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await _registry.GetChildrenAsync(parentId, cancellationToken).ConfigureAwait(false);
            return Success<IReadOnlyList<GeoLookupOption>>(
                response.Children.Select(ToOption).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable<IReadOnlyList<GeoLookupOption>>("Place search is temporarily unavailable.");
        }
    }

    private static GeoLookupOption ToOption(GeoSearchResult result) => new(
        result.Id,
        result.Name,
        result.Kind,
        result.CountryCode,
        result.CountryName,
        result.Ancestry,
        result.HasChildren,
        result.Ancestors.Select(ancestor => new GeoLookupAncestor(ancestor.Id, ancestor.Name, ancestor.Kind)).ToList());

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        Metadata(),
        message);

    private static UiCapabilityResult<T> Success<T>(T value) => new(
        UiCapabilityResultCode.Success,
        value,
        Metadata(),
        null);

    private static UiCapabilityResult<T> Retryable<T>(string message) => new(
        UiCapabilityResultCode.Retryable,
        default,
        Metadata(),
        message);

    private static UiCapabilityMetadata Metadata() => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
