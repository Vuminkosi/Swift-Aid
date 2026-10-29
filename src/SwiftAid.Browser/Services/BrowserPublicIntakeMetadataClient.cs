using System.Net.Http.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Browser.Services;

public sealed class BrowserPublicIntakeMetadataClient(HttpClient httpClient) : IPublicIntakeMetadataClient
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public Task<UiCapabilityResult<PublicIntakeMetadata>> GetMetadataAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<PublicIntakeMetadata>("public/v1/intake/metadata", "public-intake-metadata", cancellationToken);

    public Task<UiCapabilityResult<PublicIntakeCountryCatalogue>> GetCountriesAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<PublicIntakeCountryCatalogue>("public/v1/intake/metadata/countries", "public-intake-country-catalogue", cancellationToken);

    private async Task<UiCapabilityResult<T>> GetAsync<T>(
        string path,
        string capability,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await _httpClient.GetFromJsonAsync<T>(path, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return UiCapabilityResults.Unavailable<T>(capability);
            }

            var metadata = new UiCapabilityMetadata(
                capability,
                UiCapabilityResults.ContractVersion,
                UiCapabilityProvenance.LiveService,
                DateTimeOffset.UtcNow,
                IsAvailable: true);
            return new UiCapabilityResult<T>(UiCapabilityResultCode.Success, value, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<T>(capability);
        }
    }
}
