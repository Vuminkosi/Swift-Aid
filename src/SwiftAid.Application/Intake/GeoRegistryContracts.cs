using SwiftAid.Contracts.Geography;

namespace SwiftAid.Application.Intake;

/// <summary>Registry-driven geographic lookup for search-first pickers and cascading selectors.</summary>
public interface IGeographicRegistry
{
    Task<GeoSearchResponse> SearchAsync(
        string query,
        string? countryCode,
        int limit,
        CancellationToken cancellationToken);

    Task<GeoChildrenResponse> GetChildrenAsync(
        string parentId,
        CancellationToken cancellationToken);
}

/// <summary>Repeatable seed import; safe to run on every startup when the table is empty.</summary>
public interface IGeographicRegistryImporter
{
    Task<GeoRegistryImportReport> ImportAsync(CancellationToken cancellationToken);
}

public sealed record GeoRegistryImportReport(
    string RegistryVersion,
    int TotalRecords,
    int Inserted,
    int Updated,
    int MissingCoordinates);
