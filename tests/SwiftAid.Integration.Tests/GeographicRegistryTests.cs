using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Infrastructure.Intake.Geography;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Exercises the geographic registry seed import and search-first lookup without a network database.</summary>
public sealed class GeographicRegistryTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;

    public GeographicRegistryTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(DateTimeOffset.UtcNow));
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<IGeographicRegistryImporter, GeographicRegistryImporter>();
        services.AddScoped<IGeographicRegistry, GeographicRegistryService>();
        _provider = services.BuildServiceProvider();
        _dbContext = _provider.GetRequiredService<SwiftAidDbContext>();
        CreateGeoTables();
    }

    [Fact]
    public async Task Import_seeds_registry_and_second_run_is_idempotent()
    {
        var importer = _provider.GetRequiredService<IGeographicRegistryImporter>();

        var first = await importer.ImportAsync(CancellationToken.None);
        var second = await importer.ImportAsync(CancellationToken.None);

        Assert.Equal(264, first.TotalRecords);
        Assert.Equal(264, first.Inserted);
        Assert.Equal(0, first.Updated);
        Assert.Equal(264, first.MissingCoordinates);
        Assert.Equal(264, second.TotalRecords);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task Corrupted_record_is_repaired_by_reimport()
    {
        var scopeProvider = _provider;
        var importer = scopeProvider.GetRequiredService<IGeographicRegistryImporter>();
        await importer.ImportAsync(CancellationToken.None);

        var dbContext = scopeProvider.GetRequiredService<SwiftAidDbContext>();
        var record = await dbContext.GeographicLocations
            .FirstAsync(item => item.LocationId == "SZ:place:mbabane", CancellationToken.None);
        record.Name = "Tampered";
        await dbContext.SaveChangesAsync(CancellationToken.None);

        var report = await importer.ImportAsync(CancellationToken.None);

        Assert.Equal(1, report.Updated);
        Assert.Equal(0, report.Inserted);
    }

    [Fact]
    public async Task Search_returns_ranked_hit_with_full_ancestry()
    {
        var registry = _provider.GetRequiredService<IGeographicRegistry>();

        var response = await registry.SearchAsync("mbabane", null, 25, CancellationToken.None);

        Assert.Equal("southern-africa.seed1", response.RegistryVersion);
        var hit = Assert.Single(response.Results);
        Assert.Equal("SZ:place:mbabane", hit.Id);
        Assert.Equal("Mbabane", hit.Name);
        Assert.Equal(GeoLocationKind.Settlement, hit.Kind);
        Assert.Equal("SZ", hit.CountryCode);
        Assert.Equal("Eswatini", hit.CountryName);
        Assert.Contains("Hhohho", hit.Ancestry, StringComparison.Ordinal);
        Assert.Contains("Eswatini", hit.Ancestry, StringComparison.Ordinal);
        Assert.Equal(
            ["country:SZ", "SZ:adm1:hhohho"],
            hit.Ancestors.Select(ancestor => ancestor.Id));
        Assert.Null(hit.Latitude);
        Assert.Null(hit.Longitude);
    }

    [Fact]
    public async Task Search_respects_country_scope_and_result_cap()
    {
        var registry = _provider.GetRequiredService<IGeographicRegistry>();

        var scoped = await registry.SearchAsync("an", "ZW", 5, CancellationToken.None);

        Assert.All(scoped.Results, result => Assert.Equal("ZW", result.CountryCode));
        Assert.True(scoped.Results.Count <= 5, "Results must respect the requested cap.");
        Assert.True(scoped.TotalCount >= scoped.Results.Count, "Total must cover the returned window.");
    }

    [Fact]
    public async Task Same_name_region_and_settlement_carry_disambiguating_ancestry()
    {
        var registry = _provider.GetRequiredService<IGeographicRegistry>();

        var response = await registry.SearchAsync("manzini", null, 25, CancellationToken.None);

        Assert.Equal(2, response.Results.Count);
        var ancestries = response.Results.Select(result => result.Ancestry).ToArray();
        Assert.NotEqual(ancestries[0], ancestries[1]);
        Assert.All(response.Results, result =>
            Assert.Contains("Manzini", result.Ancestry, StringComparison.Ordinal));
        Assert.Contains(response.Results, result => result.Kind == GeoLocationKind.Region);
        Assert.Contains(response.Results, result => result.Kind == GeoLocationKind.Settlement);
    }

    [Fact]
    public async Task Children_lists_parent_settlements_and_unknown_parent_is_empty()
    {
        var registry = _provider.GetRequiredService<IGeographicRegistry>();

        var children = await registry.GetChildrenAsync("SZ:adm1:hhohho", CancellationToken.None);

        Assert.NotEmpty(children.Children);
        Assert.Equal("SZ:adm1:hhohho", children.ParentId);
        Assert.All(children.Children, child =>
            Assert.EndsWith("Hhohho", child.Ancestry, StringComparison.Ordinal));
        Assert.Contains(children.Children, child => child.Name == "Mbabane");

        var missing = await registry.GetChildrenAsync("XX:adm1:nowhere", CancellationToken.None);

        Assert.Empty(missing.Children);
    }

    private void CreateGeoTables()
    {
        _dbContext.Database.ExecuteSqlRaw("""
            CREATE TABLE locations (
                location_id TEXT NOT NULL PRIMARY KEY,
                country_code TEXT NOT NULL,
                name TEXT NOT NULL,
                location_kind TEXT NOT NULL,
                parent_id TEXT,
                latitude REAL,
                longitude REAL,
                source TEXT NOT NULL,
                verification TEXT NOT NULL,
                is_active INTEGER NOT NULL,
                imported_at_utc TEXT NOT NULL
            );
            CREATE INDEX ix_geo_locations_country_name ON locations (country_code, name);
            CREATE INDEX ix_geo_locations_parent ON locations (parent_id);
            PRAGMA foreign_keys = ON;
            """);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
