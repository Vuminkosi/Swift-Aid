using System.Net.Http.Json;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.Client.Presentation;

/// <summary>Resolves a case's public registry place without treating its coordinates as a case location.</summary>
public sealed class SurveillancePlaceLookupClient(HttpClient httpClient)
{
    public async Task<GeoSearchResult?> FindAsync(string country, string? placeId, string? region, string? area,
        CancellationToken cancellationToken = default)
    {
        var name = !string.IsNullOrWhiteSpace(area) ? area : region;
        if (country.Length != 2 || !country.All(char.IsAsciiLetter) || string.IsNullOrWhiteSpace(name) ||
            name.Length is < 2 or > 120 || region?.Length > 120 || placeId?.Length > 160 || httpClient.BaseAddress is null)
            return null;

        try
        {
            var path = $"/public/v1/intake/geo/search?q={Uri.EscapeDataString(name)}&country={Uri.EscapeDataString(country)}&limit=20";
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<GeoSearchResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result is null || string.IsNullOrWhiteSpace(placeId) && result.TotalCount > result.Results.Count)
                return null;
            var matches = result?.Results.Where(item =>
                string.Equals(item.CountryCode, country, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(placeId) ? string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) : item.Id == placeId) &&
                (string.IsNullOrWhiteSpace(region) || string.Equals(item.Name, region, StringComparison.OrdinalIgnoreCase) ||
                 item.Ancestors.Any(parent => string.Equals(parent.Name, region, StringComparison.OrdinalIgnoreCase)))).ToArray();
            return matches is [{ Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 } match] ? match : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}
