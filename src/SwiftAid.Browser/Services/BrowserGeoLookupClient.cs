using System.Net.Http.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.Browser.Services;

/// <summary>Serves registry location lookup to the guest shell over the public
/// intake geo endpoints. No sign-in required; failures stay user-facing copy.</summary>
public sealed class BrowserGeoLookupClient(HttpClient httpClient) : IGeoLookupClient
{
    private const string Capability = "geo-lookup";

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

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
            var path = "public/v1/intake/geo/search?q=" + Uri.EscapeDataString(query.Trim()) +
                "&country=" + Uri.EscapeDataString(countryCode?.Trim() ?? string.Empty);
            var response = await _httpClient.GetFromJsonAsync<GeoSearchResponse>(path, cancellationToken)
                .ConfigureAwait(false);
            if (response?.Results is null)
            {
                return Retryable<IReadOnlyList<GeoLookupOption>>("Place search is temporarily unavailable.");
            }

            return Success<IReadOnlyList<GeoLookupOption>>(response.Results.Select(ToOption).ToList());
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
            var path = "public/v1/intake/geo/children?parentId=" + Uri.EscapeDataString(parentId);
            var response = await _httpClient.GetFromJsonAsync<GeoChildrenResponse>(path, cancellationToken)
                .ConfigureAwait(false);
            if (response?.Children is null)
            {
                return Retryable<IReadOnlyList<GeoLookupOption>>("Place search is temporarily unavailable.");
            }

            return Success<IReadOnlyList<GeoLookupOption>>(response.Children.Select(ToOption).ToList());
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
