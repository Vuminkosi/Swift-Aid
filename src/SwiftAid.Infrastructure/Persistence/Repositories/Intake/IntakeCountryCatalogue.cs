using SwiftAid.Application.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>Validates reports against the exact hash-pinned catalogue snapshot shown to reporters.</summary>
public sealed class IntakeCountryCatalogue(IIntakePublicMetadataProvider metadataProvider) : IIntakeCountryCatalogue
{
    private readonly IIntakePublicMetadataProvider _metadataProvider =
        metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));

    public async Task<bool> IsKnownCountryAsync(
        string countryCode,
        string catalogueVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(catalogueVersion))
        {
            return false;
        }

        var catalogue = await _metadataProvider.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(catalogue.Version, catalogueVersion, StringComparison.Ordinal) &&
               catalogue.Countries.Any(country => string.Equals(country.Code, countryCode, StringComparison.Ordinal));
    }
}
