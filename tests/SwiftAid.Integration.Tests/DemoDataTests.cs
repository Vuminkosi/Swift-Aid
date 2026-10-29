using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Demo;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Intake;
using SwiftAid.Contracts.Surveillance;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Demo;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Exercises the development demo dataset: strict CSV parsing, option
/// validation, production refusal, and the Sqlite importer-to-aggregate chain.
/// </summary>
public sealed class DemoDataTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;

    public DemoDataTests()
    {
        _connection.Open();
        // The model expresses Postgres check constraints with btrim(); map it to trim for Sqlite.
        _connection.CreateFunction("btrim", (string? value) => value is null ? null : value.Trim());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(new DemoDataOptions { Enabled = true, MaxCases = 1000 });
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<IDemoCaseImporter, DemoCaseImporter>();
        _provider = services.BuildServiceProvider();
        _dbContext = _provider.GetRequiredService<SwiftAidDbContext>();
        CreateDemoTables(_dbContext);
    }

    [Fact]
    public void Parser_AcceptsValidRowsAndReportsInvalidOnes()
    {
        var csv = string.Join("\n",
            "country_code,region_id,sector,kind,workflow,created_days_ago,sensitivity_code",
            "SZ,SZ-HH,HumanHealth,Human,Active,3,standard",
            "ZW,,AnimalHealth,Animal,Reported,300,standard",
            "BAD ROW",
            "SZ,SZ-HH,HumanHealth,Animal,Active,3,standard",
            "SZ,SZ-HH,HumanHealth,Human,Active,999,standard");

        var (rows, errors) = DemoDataCsvParser.Parse(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3, errors.Count);
        Assert.Equal("SZ", rows[0].CountryCode);
        Assert.Equal("SZ-HH", rows[0].RegionId);
        Assert.Null(rows[1].RegionId);
    }

    [Fact]
    public void Options_RejectOutOfRangeMaxCases()
    {
        Assert.Throws<InvalidOperationException>(() => new DemoDataOptions { MaxCases = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DemoDataOptions { MaxCases = 5001 }.Validate());
        new DemoDataOptions { MaxCases = 1000 }.Validate();
    }

    [Fact]
    public async Task Importer_SeedsDemoScope_RerunIsIdempotent_AndAggregatesReadBack()
    {
        var importer = _provider.GetRequiredService<IDemoCaseImporter>();

        var first = await importer.ImportAsync(CancellationToken.None);
        var second = await importer.ImportAsync(CancellationToken.None);

        Assert.True(first.TotalRecords > 500, $"Expected over 500 demo records, found {first.TotalRecords}.");
        Assert.Equal(first.TotalRecords, first.Inserted);
        Assert.Equal(0, first.Invalid);
        Assert.False(first.AlreadySeeded);
        Assert.True(second.AlreadySeeded);
        Assert.Equal(0, second.Inserted);

        var demoCases = await _dbContext.ManagedCases
            .AsNoTracking()
            .Where(item => item.OrganizationId == DemoCaseImporter.DemoOrganizationId)
            .ToArrayAsync();
        Assert.Equal(first.Inserted, demoCases.Length);
        Assert.All(demoCases, item => Assert.Equal(DemoCaseImporter.DemoActorId, item.AccountableOwnerActorId));

        var grantGroups = await _dbContext.AccessGrantGroups
            .AsNoTracking()
            .Where(item => item.ActorId == DemoCaseImporter.DemoActorId)
            .ToArrayAsync();
        Assert.NotEmpty(grantGroups);
        Assert.All(grantGroups, group => Assert.Equal(DemoCaseImporter.DemoOrganizationId, group.OrganizationId));
        Assert.Contains(grantGroups, group =>
            group.Sector == Sector.HumanHealth && group.CountryCode == "SZ");
        Assert.Contains(grantGroups, group =>
            group.Sector == Sector.AnimalHealth && group.CountryCode == "ZW");
        var operations = await _dbContext.AccessGrantOperations
            .AsNoTracking()
            .Where(item => grantGroups.Select(group => group.GrantGroupId).Contains(item.GrantGroupId))
            .ToArrayAsync();
        Assert.All(operations, operation => Assert.Equal(CaseOperation.ReadCase, operation.Operation));

        // Sqlite cannot translate DateTimeOffset comparisons, so this chain uses a
        // test-local store that reads the same imported rows; the real store's date
        // predicate is covered by the Postgres opt-in tests.
        var store = new SqliteBackedStore(_dbContext);
        var storeRows = await store.GetOwnedCaseAreasAsync(
            DemoCaseImporter.DemoActorId, DateTimeOffset.UtcNow.AddDays(-400), 1001, CancellationToken.None);
        Assert.Equal(first.Inserted, storeRows.Count);

        var query = new SurveillanceMapAggregateQuery(
            store,
            new AllowPolicy(),
            new StubUnitOfWork(),
            new StubMetadata(),
            TimeProvider.System);
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, DemoCaseImporter.DemoActorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "unit-test")));

        var aggregate = await query.GetAsync(actor, null, TimeSpan.FromDays(90), CancellationToken.None);

        Assert.Equal(SurveillanceAggregateAccess.Allowed, aggregate.Access);
        Assert.NotEmpty(aggregate.Areas);
        Assert.All(aggregate.Areas, area => Assert.True(area.IsValid));
        var eSwatini = Assert.Single(aggregate.Areas, area =>
            area.Precision == SurveillanceAggregatePrecision.Country && area.AreaCode == "SZ");
        Assert.Equal("Eswatini", eSwatini.AreaLabel);
        Assert.False(eSwatini.IsSuppressed);
        Assert.True((eSwatini.HumanCount ?? 0) + (eSwatini.AnimalCount ?? 0) >= 5);
    }

    [Fact]
    public async Task StartupImporter_SkipsWhenDisabled()
    {
        await using var host = CreateIsolatedHost(enabled: false, isProduction: false);
        await host.Importer.StartAsync(CancellationToken.None);
        Assert.False(await HasDemoCasesAsync(host.DbContext));
    }

    [Fact]
    public async Task StartupImporter_SkipsWhenProductionEvenIfEnabled()
    {
        await using var host = CreateIsolatedHost(enabled: true, isProduction: true);
        await host.Importer.StartAsync(CancellationToken.None);
        Assert.False(await HasDemoCasesAsync(host.DbContext));
    }

    [Fact]
    public async Task StartupImporter_ImportsWhenEnabledOutsideProduction()
    {
        await using var host = CreateIsolatedHost(enabled: true, isProduction: false);
        await host.Importer.StartAsync(CancellationToken.None);
        Assert.True(await HasDemoCasesAsync(host.DbContext));
    }

    private sealed record IsolatedHost(
        ServiceProvider Provider, SwiftAidDbContext DbContext, DemoDataStartupImporter Importer)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    private static IsolatedHost CreateIsolatedHost(bool enabled, bool isProduction)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new DemoDataOptions { Enabled = enabled, MaxCases = 1000 });
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(isProduction));
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IDemoCaseImporter, DemoCaseImporter>();
        services.AddSingleton<IMapAggregateQuery, StubAggregateQuery>();
        var provider = services.BuildServiceProvider();
        var dbContext = provider.GetRequiredService<SwiftAidDbContext>();
        CreateDemoTables(dbContext);
        var importer = new DemoDataStartupImporter(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<DemoDataOptions>(),
            NullLogger<DemoDataStartupImporter>.Instance);
        return new IsolatedHost(provider, dbContext, importer);
    }

    private static async Task<bool> HasDemoCasesAsync(SwiftAidDbContext dbContext) =>
        await dbContext.ManagedCases.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == DemoCaseImporter.DemoOrganizationId);

    private static void CreateDemoTables(SwiftAidDbContext dbContext)
    {
        dbContext.Database.ExecuteSqlRaw("""
            CREATE TABLE actors (
                actor_id TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL,
                expires_at_utc TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0);
            CREATE TABLE actor_access_states (
                actor_id TEXT NOT NULL PRIMARY KEY,
                access_epoch INTEGER NOT NULL,
                version INTEGER NOT NULL,
                updated_at_utc TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0);
            CREATE TABLE organizations (
                organization_id TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0);
            CREATE TABLE workspaces (
                workspace_id TEXT NOT NULL PRIMARY KEY,
                organization_id TEXT NOT NULL,
                status TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0);
            CREATE TABLE organization_memberships (
                membership_id TEXT NOT NULL PRIMARY KEY,
                actor_id TEXT NOT NULL,
                organization_id TEXT NOT NULL,
                status TEXT NOT NULL,
                valid_from_utc TEXT NOT NULL,
                expires_at_utc TEXT NULL,
                granted_by_actor_id TEXT NOT NULL,
                revoked_by_actor_id TEXT NULL,
                change_reason TEXT NULL,
                version INTEGER NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0);
            CREATE TABLE access_grant_groups (
                grant_group_id TEXT NOT NULL PRIMARY KEY,
                membership_id TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                organization_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                country_code TEXT NOT NULL,
                sector TEXT NOT NULL,
                sensitivity_code TEXT NOT NULL,
                can_access_jurisdictional_cases INTEGER NOT NULL,
                status TEXT NOT NULL,
                valid_from_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                granted_by_actor_id TEXT NOT NULL,
                revoked_by_actor_id TEXT NULL,
                change_reason TEXT NULL,
                version INTEGER NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0);
            CREATE TABLE access_grant_group_operations (
                grant_group_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0,
                PRIMARY KEY (grant_group_id, operation));
            CREATE TABLE managed_cases (
                case_id TEXT NOT NULL PRIMARY KEY,
                organization_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                country_code TEXT NOT NULL,
                kind TEXT NOT NULL,
                sector TEXT NOT NULL,
                sensitivity_code TEXT NOT NULL,
                accountable_owner_actor_id TEXT NOT NULL,
                resource_version INTEGER NOT NULL,
                aggregate_version INTEGER NOT NULL,
                report_revision INTEGER NOT NULL,
                workflow TEXT NOT NULL,
                workflow_version INTEGER NOT NULL,
                classification TEXT NOT NULL,
                classification_version INTEGER NOT NULL,
                laboratory TEXT NOT NULL,
                laboratory_version INTEGER NOT NULL,
                outcome_state TEXT NOT NULL,
                outcome_value TEXT NULL,
                outcome_version INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0);
            CREATE TABLE managed_case_regions (
                case_id TEXT NOT NULL,
                region_id TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0,
                PRIMARY KEY (case_id, region_id));
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _provider.DisposeAsync();
        await _connection.CloseAsync();
    }

    private sealed class AllowPolicy : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(
            ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(true, AccessDecisionCode.Allowed, null));

        public ValueTask<AccessDecision> AuthorizeAllAsync(
            ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(true, AccessDecisionCode.Allowed, null));
    }

    private sealed class StubLease : ICaseCommandLease
    {
        public Guid CaseId => Guid.Empty;
        public CaseLeaseMode Mode => CaseLeaseMode.Read;
        public System.Collections.Immutable.ImmutableArray<string> LockedActorIds => [];
        public bool IsActive => true;
        public ValueTask CommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubUnitOfWork : ICaseCommandUnitOfWork
    {
        public ValueTask<ICaseCommandLease> BeginCaseAsync(
            ActorContext actor,
            Guid caseId,
            System.Collections.Immutable.ImmutableArray<string> additionalActorHints,
            CaseLeaseMode mode,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ICaseCommandLease>(new StubLease());
    }

    private sealed class SqliteBackedStore(SwiftAidDbContext dbContext) : IMapAggregateStore
    {
        public async Task<IReadOnlyList<MapAggregateCaseRow>> GetOwnedCaseAreasAsync(
            string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct)
        {
            var cases = await dbContext.ManagedCases.AsNoTracking()
                .Where(item => item.AccountableOwnerActorId == ownerActorId)
                .OrderBy(item => item.CaseId)
                .ToArrayAsync(ct);
            var caseIds = cases.Select(item => item.CaseId).ToArray();
            var regions = await dbContext.ManagedCaseRegions.AsNoTracking()
                .Where(region => caseIds.Contains(region.CaseId))
                .ToArrayAsync(ct);
            var regionsByCase = regions
                .GroupBy(region => region.CaseId)
                .ToDictionary(group => group.Key, group => group.Select(region => region.RegionId).ToArray());
            return cases
                .Where(item => item.CreatedAtUtc >= sinceUtc)
                .Take(take)
                .Select(item => new MapAggregateCaseRow(
                    item.CaseId,
                    item.CountryCode,
                    regionsByCase.TryGetValue(item.CaseId, out var regionIds)
                        ? regionIds
                        : Array.Empty<string>(),
                    item.Sector,
                    item.CreatedAtUtc))
                .ToArray();
        }
    }

    private sealed class StubMetadata : IIntakePublicMetadataProvider
    {
        public PublicIntakeMetadata GetMetadata() => new("test", "test", "test", "test", "test");

        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) =>
            Task.FromResult(new PublicIntakeCountryCatalogue(
                "test",
                "test",
                [
                    new PublicIntakeCountry("SZ", "Eswatini"),
                    new PublicIntakeCountry("ZW", "Zimbabwe"),
                    new PublicIntakeCountry("ZA", "South Africa")
                ]));
    }

    private sealed class StubAggregateQuery : IMapAggregateQuery
    {
        public Task<SurveillanceAggregateResult> GetAsync(
            ActorContext actor, string? regionCode, TimeSpan window, CancellationToken ct) =>
            Task.FromResult(new SurveillanceAggregateResult(
                SurveillanceAggregateAccess.Allowed, [], DateTimeOffset.UtcNow, false));
    }

    private sealed class StubHostEnvironment(bool isProduction) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } =
            isProduction ? Environments.Production : Environments.Development;
        public string ApplicationName { get; set; } = "SwiftAid.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
