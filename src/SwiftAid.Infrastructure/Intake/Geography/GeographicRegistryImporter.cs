using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Intake;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Intake.Geography;

/// <summary>Idempotent import of the curated Southern Africa location seed into the canonical registry.</summary>
public sealed class GeographicRegistryImporter(
    SwiftAidDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<GeographicRegistryImporter> logger) : IGeographicRegistryImporter
{
    public const string ResourceName =
        "SwiftAid.Infrastructure.Resources.Catalogues.location-registry.southern-africa.seed1.json";

    public const string RegistryVersion = "southern-africa.seed1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<GeographicRegistryImporter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<GeoRegistryImportReport> ImportAsync(CancellationToken cancellationToken)
    {
        var seed = await LoadSeedAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var inserted = 0;
        var updated = 0;
        var missingCoordinates = 0;

        var existing = await _dbContext.GeographicLocations
            .ToDictionaryAsync(item => item.LocationId, item => item, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in seed.Locations)
        {
            if (string.IsNullOrWhiteSpace(record.Id) ||
                string.IsNullOrWhiteSpace(record.CountryCode) ||
                string.IsNullOrWhiteSpace(record.Name) ||
                !GeographicRegistryMapper.TryMapKind(record.LocationType, out var kind))
            {
                _logger.LogWarning(
                    "Skipping location seed record with id {Id}: missing identity or unknown kind {Kind}.",
                    record.Id,
                    record.LocationType);
                continue;
            }

            if (record.Latitude is null || record.Longitude is null)
            {
                missingCoordinates++;
            }

            if (!existing.TryGetValue(record.Id, out var current))
            {
                _dbContext.GeographicLocations.Add(new GeographicLocationEntity
                {
                    LocationId = record.Id,
                    CountryCode = record.CountryCode,
                    Name = record.Name.Trim(),
                    LocationKind = kind.ToString(),
                    ParentId = string.IsNullOrWhiteSpace(record.ParentId) ? null : record.ParentId,
                    Latitude = record.Latitude,
                    Longitude = record.Longitude,
                    Source = string.IsNullOrWhiteSpace(record.Source) ? "curated seed" : record.Source,
                    Verification = string.IsNullOrWhiteSpace(record.Verification) ? "unverified" : record.Verification,
                    IsActive = true,
                    ImportedAtUtc = now
                });
                inserted++;
                continue;
            }

            var changed = !string.Equals(current.Name, record.Name.Trim(), StringComparison.Ordinal) ||
                !string.Equals(current.CountryCode, record.CountryCode, StringComparison.Ordinal) ||
                !string.Equals(current.LocationKind, kind.ToString(), StringComparison.Ordinal) ||
                !string.Equals(current.ParentId, record.ParentId, StringComparison.Ordinal) ||
                current.Latitude != record.Latitude ||
                current.Longitude != record.Longitude;
            if (!changed)
            {
                continue;
            }

            current.CountryCode = record.CountryCode;
            current.Name = record.Name.Trim();
            current.LocationKind = kind.ToString();
            current.ParentId = string.IsNullOrWhiteSpace(record.ParentId) ? null : record.ParentId;
            current.Latitude = record.Latitude;
            current.Longitude = record.Longitude;
            current.Verification = string.IsNullOrWhiteSpace(record.Verification) ? current.Verification : record.Verification;
            current.ImportedAtUtc = now;
            updated++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var total = existing.Count + inserted;
        _logger.LogInformation(
            "Location registry import complete: {Total} records ({Inserted} inserted, {Updated} updated, {Missing} without coordinates).",
            total,
            inserted,
            updated,
            missingCoordinates);
        return new GeoRegistryImportReport(RegistryVersion, total, inserted, updated, missingCoordinates);
    }

    private static async Task<LocationSeedSnapshot> LoadSeedAsync(CancellationToken cancellationToken)
    {
        await using var resource = typeof(GeographicRegistryImporter).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The location registry seed resource is missing.");
        await using var buffer = new MemoryStream();
        await resource.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        try
        {
            var snapshot = JsonSerializer.Deserialize<LocationSeedSnapshot>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The location registry seed is invalid.");
            if (snapshot.Locations is not { Count: > 0 })
            {
                throw new InvalidDataException("The location registry seed contains no locations.");
            }

            return snapshot;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record LocationSeedSnapshot(List<LocationSeedRecord> Locations);

    private sealed record LocationSeedRecord(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("country_code")] string? CountryCode,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("location_type")] string? LocationType,
        [property: JsonPropertyName("verification")] string? Verification,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("latitude")] double? Latitude,
        [property: JsonPropertyName("longitude")] double? Longitude);
}
