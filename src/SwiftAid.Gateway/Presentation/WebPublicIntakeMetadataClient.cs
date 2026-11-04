using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Server-side public intake metadata for Blazor Server: reads the versioned catalogue from the application metadata provider.</summary>
public sealed class WebPublicIntakeMetadataClient(IIntakePublicMetadataProvider metadata) : IPublicIntakeMetadataClient
{
    private readonly IIntakePublicMetadataProvider _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

    public Task<UiCapabilityResult<PublicIntakeMetadata>> GetMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(new UiCapabilityResult<PublicIntakeMetadata>(
                UiCapabilityResultCode.Success,
                _metadata.GetMetadata(),
                LiveMetadata("public-intake-metadata")));
        }
        catch (Exception)
        {
            return Task.FromResult(UiCapabilityResults.Unavailable<PublicIntakeMetadata>("public-intake-metadata"));
        }
    }

    public async Task<UiCapabilityResult<PublicIntakeCountryCatalogue>> GetCountriesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var catalogue = await _metadata.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
            return new UiCapabilityResult<PublicIntakeCountryCatalogue>(
                UiCapabilityResultCode.Success,
                catalogue,
                LiveMetadata("public-intake-metadata"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<PublicIntakeCountryCatalogue>("public-intake-metadata");
        }
    }

    private static UiCapabilityMetadata LiveMetadata(string capability) => new(
        capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
