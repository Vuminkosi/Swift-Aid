using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Infrastructure.Intake.Geography;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Gateway.Ellai;
using System.Data.Common;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;

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
        Assert.Equal(153, first.MissingCoordinates);
        Assert.Equal(264, second.TotalRecords);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task Reimport_updates_missing_public_coordinates_and_provenance_without_duplicates()
    {
        var importer = _provider.GetRequiredService<IGeographicRegistryImporter>();
        await importer.ImportAsync(CancellationToken.None);
        var place = await _dbContext.GeographicLocations.SingleAsync(item => item.LocationId == "SZ:place:sidvokodvo");
        place.Latitude = null;
        place.Longitude = null;
        place.Source = "original seed";
        await _dbContext.SaveChangesAsync();

        var result = await importer.ImportAsync(CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Inserted);
        Assert.NotNull(place.Latitude);
        Assert.NotNull(place.Longitude);
        Assert.Contains("geonames.org", place.Source);
        Assert.Equal(0, (await importer.ImportAsync(CancellationToken.None)).Updated);
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
    public async Task ReadOnlySearchDoesNotSeedAndMatchesNormalSearchAfterInitialization()
    {
        var registry = Assert.IsType<GeographicRegistryService>(_provider.GetRequiredService<IGeographicRegistry>());

        Assert.Null(await registry.SearchIfInitializedAsync("mbabane", "SZ", 5, default));
        Assert.Empty(await _dbContext.GeographicLocations.ToListAsync());
        var initialized = await registry.SearchAsync("mbabane", "SZ", 5, default);
        var readOnly = await registry.SearchIfInitializedAsync("mbabane", "SZ", 5, default);

        Assert.NotNull(readOnly);
        Assert.Equal(initialized.RegistryVersion, readOnly.RegistryVersion);
        Assert.Equal(initialized.TotalCount, readOnly.TotalCount);
        Assert.Equal(initialized.Results.Select(item => item.Id), readOnly.Results.Select(item => item.Id));
        Assert.Equal(264, await _dbContext.GeographicLocations.CountAsync());
    }

    [Fact]
    public async Task IsolatedSearchPreservesFirstUseSeeding()
    {
        var fallback = _provider.GetRequiredService<IGeographicRegistry>();
        var isolated = new EllaiIsolatedGeographicRegistry(_provider, new EllaiIsolatedToolCalls(), fallback);

        var result = await isolated.SearchAsync("mbabane", "SZ", 5, default);

        Assert.Contains(result.Results, item => item.Id == "SZ:place:mbabane");
        Assert.Equal(264, await _dbContext.GeographicLocations.CountAsync());
    }

    [Fact]
    public async Task CancelledInitializedReadReturnsBeforeNoncooperativeDatabaseCallAndDoesNotSeed()
    {
        var fallback = _provider.GetRequiredService<IGeographicRegistry>();
        await fallback.SearchAsync("mbabane", "SZ", 5, default);
        var hold = new HeldReadInterceptor();
        var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System);
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite(_connection).AddInterceptors(hold));
        services.AddScoped<IGeographicRegistryImporter, GeographicRegistryImporter>();
        services.AddScoped<IGeographicRegistry, GeographicRegistryService>();
        await using var provider = services.BuildServiceProvider();
        var calls = new EllaiIsolatedToolCalls(1);
        var isolated = new EllaiIsolatedGeographicRegistry(provider, calls, fallback);
        using var cancellation = new CancellationTokenSource();
        var search = isolated.SearchAsync("mbabane", "SZ", 5, cancellation.Token);
        try
        {
            await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(hold.Released.Task.IsCompleted);
        }
        finally
        {
            hold.Released.TrySetResult();
        }
        await calls.RunAsync(_ => Task.FromResult(true), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(264, await _dbContext.GeographicLocations.CountAsync());
    }

    [Fact]
    public async Task GatewayWorkerTimesOutHeldInitializedLocationRead()
    {
        await _provider.GetRequiredService<IGeographicRegistry>().SearchAsync("mbabane", "SZ", 5, default);
        var hold = new HeldReadInterceptor();
        var store = new InMemoryEllaiSessionStore();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "location-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture")));
        var session = new EllaiSessionRecord("location-timeout", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, actor.ActorId, "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        var work = new EllaiUtteranceWork(session.SessionId, "scope", "Find Mbabane", "public.product-help", null,
            EllaiMode.Ask, 1, "cycle", actor, true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
            EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string> { EllaiToolNames.Locations },
            EllaiConsentService.PublicProductHelpPurpose, "turn");
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
            work.ClientUtteranceId, default, EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId));
        var consent = new EllaiConsentService();
        using var model = new LocationToolClient();
        var registrations = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System);
        registrations.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite(_connection).AddInterceptors(hold));
        registrations.AddScoped<IGeographicRegistryImporter, GeographicRegistryImporter>();
        registrations.AddScoped<IGeographicRegistry, GeographicRegistryService>();
        registrations.AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store);
        registrations.AddSingleton<EllaiSessionResponseStore>().AddSingleton(consent);
        registrations.AddSingleton<EllaiUsageLedger>().AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        registrations.AddScoped(sp => new EllaiToolBroker(new EllaiPolicyService(), consent,
            geography: sp.GetRequiredService<IGeographicRegistry>()));
        registrations.AddScoped<IEllaiReasoningExecutor>(sp => new EllaiFrameworkExecutor(model,
            new EllaiPromptBuilder("Synthetic policy", "fixture"), sp.GetRequiredService<EllaiToolBroker>(),
            sp.GetRequiredService<EllaiUsageLedger>(), sp.GetRequiredService<IEllaiUsageStore>(), TimeSpan.FromMilliseconds(250)));
        await using var provider = registrations.BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(provider, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await hold.Started.Task.WaitAsync(timeout.Token);
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(20, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(session.SessionId, actor.ActorId, timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.NotEqual("Completed", turn.Outcome);
            Assert.False(hold.Released.Task.IsCompleted);
        }
        finally
        {
            hold.Released.TrySetResult();
            await worker.StopAsync(default);
        }
    }

    private sealed class LocationToolClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("location", EllaiToolNames.Locations,
                    new Dictionary<string, object?> { ["q"] = "mbabane", ["country"] = "SZ" })])));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Contents);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HeldReadInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Released.Task;
            return result;
        }
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
