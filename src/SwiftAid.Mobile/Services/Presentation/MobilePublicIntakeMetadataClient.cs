using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Uses the configured public intake transport for mobile catalogue metadata.</summary>
public sealed class MobilePublicIntakeMetadataClient(IGuestIntakeTransport transport) : IPublicIntakeMetadataClient
{
    public Task<UiCapabilityResult<PublicIntakeMetadata>> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(transport.GetMetadataAsync, cancellationToken);

    public Task<UiCapabilityResult<PublicIntakeCountryCatalogue>> GetCountriesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(transport.GetCountriesAsync, cancellationToken);

    private static async Task<UiCapabilityResult<T>> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var value = await read(cancellationToken).ConfigureAwait(false);
            return value is null ? UiCapabilityResults.Unavailable<T>("public-intake-metadata") : new(
                UiCapabilityResultCode.Success, value,
                new UiCapabilityMetadata("public-intake-metadata", UiCapabilityResults.ContractVersion,
                    UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return UiCapabilityResults.Unavailable<T>("public-intake-metadata"); }
    }
}
