using System.Net;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Geography;
using SwiftAid.Mobile.Services.Http;

namespace SwiftAid.Mobile.Services.Account;

/// <summary>Registry location lookup through the gateway cookie session. Works for signed-out guests.</summary>
public sealed class HttpGeoLookupClient(CookieSessionHttpClient session) : IGeoLookupClient
{
    private const string Capability = "geo-lookup";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CookieSessionHttpClient _session = session ?? throw new ArgumentNullException(nameof(session));

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
            var path = $"/public/v1/intake/geo/search?q={Uri.EscapeDataString(query.Trim())}";
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                path += $"&country={Uri.EscapeDataString(countryCode.Trim())}";
            }

            using var response = await _session.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return Invalid<IReadOnlyList<GeoLookupOption>>("Type at least two characters to search places.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable<IReadOnlyList<GeoLookupOption>>("Place search is temporarily unavailable.");
            }

            var payload = await ReadAsync<GeoSearchResponse>(response, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? Retryable<IReadOnlyList<GeoLookupOption>>("Place search is temporarily unavailable.")
                : Success<IReadOnlyList<GeoLookupOption>>(payload.Results.Select(ToOption).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Retryable<IReadOnlyList<GeoLookupOption>>(ClientRequestFailure.MessageFor(ex, "Searching places"));
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            return Retryable<IReadOnlyList<GeoLookupOption>>(ClientRequestFailure.MessageFor(ex, "Searching places"));
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
            using var response = await _session.GetAsync(
                $"/public/v1/intake/geo/children?parentId={Uri.EscapeDataString(parentId)}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Retryable<IReadOnlyList<GeoLookupOption>>("Place search is temporarily unavailable.");
            }

            var payload = await ReadAsync<GeoChildrenResponse>(response, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? Retryable<IReadOnlyList<GeoLookupOption>>("Place search is temporarily unavailable.")
                : Success<IReadOnlyList<GeoLookupOption>>(payload.Children.Select(ToOption).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Retryable<IReadOnlyList<GeoLookupOption>>(ClientRequestFailure.MessageFor(ex, "Searching places"));
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            return Retryable<IReadOnlyList<GeoLookupOption>>(ClientRequestFailure.MessageFor(ex, "Searching places"));
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
        result.HasChildren);

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

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
