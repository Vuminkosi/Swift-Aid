using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Infrastructure.Intake;

/// <summary>Serves a hash-pinned public snapshot without consulting report or organization data.</summary>
public sealed class IntakePublicMetadataProvider : IIntakePublicMetadataProvider
{
    private const string CatalogueResourceName =
        "SwiftAid.Infrastructure.Resources.Catalogues.country-areas.m49.2026-10-27.1.json";
    private readonly IntakeMetadataOptions _options;
    private readonly Lazy<Task<PublicIntakeCountryCatalogue>> _countries;

    public IntakePublicMetadataProvider(IntakeMetadataOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _countries = new Lazy<Task<PublicIntakeCountryCatalogue>>(
            LoadCountriesAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public PublicIntakeMetadata GetMetadata() => new(
        _options.CatalogueVersion,
        _options.CatalogueVersion,
        _options.CatalogueReference,
        _options.PrivacyNoticeVersion,
        _options.PrivacyNoticeReference);

    public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) =>
        _countries.Value.WaitAsync(ct);

    private async Task<PublicIntakeCountryCatalogue> LoadCountriesAsync()
    {
        await using var resource = typeof(IntakePublicMetadataProvider).Assembly
            .GetManifestResourceStream(CatalogueResourceName)
            ?? throw new InvalidDataException("The intake country catalogue resource is missing.");
        await using var buffer = new MemoryStream();
        await resource.CopyToAsync(buffer).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(hash, _options.CatalogueSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The intake country catalogue hash does not match configuration.");
            }

            var snapshot = JsonSerializer.Deserialize<CountryCatalogueSnapshot>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The intake country catalogue is invalid.");
            if (!string.Equals(snapshot.Version, _options.CatalogueVersion, StringComparison.Ordinal) ||
                snapshot.Countries is not { Count: > 0 } ||
                snapshot.Countries.Any(country => country.Code is not { Length: 2 } ||
                    country.Code.Any(character => character is < 'A' or > 'Z') ||
                    string.IsNullOrWhiteSpace(country.Label) || country.Label.Length > 200) ||
                snapshot.Countries.Select(country => country.Code).Distinct(StringComparer.Ordinal).Count() !=
                    snapshot.Countries.Count)
            {
                throw new InvalidDataException("The intake country catalogue does not match its configured version.");
            }

            var countries = snapshot.Countries
                .Select(country => new PublicIntakeCountry(country.Code, country.Label))
                .OrderBy(country => country.Code, StringComparer.Ordinal)
                .ToArray();
            return new PublicIntakeCountryCatalogue(
                snapshot.Version,
                _options.CatalogueReference,
                countries);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private sealed record CountryCatalogueSnapshot(string Version, List<CountryRow> Countries);

    private sealed record CountryRow(string Code, string Label);
}
