using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Exercises profile validation and upsert without a network database.</summary>
public sealed class UserProfileServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;
    private readonly ManualProfileTimeProvider _timeProvider = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    public UserProfileServiceTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<IUserProfileService, UserProfileService>();
        _provider = services.BuildServiceProvider();
        _dbContext = _provider.GetRequiredService<SwiftAidDbContext>();
        CreateTables();
    }

    [Fact]
    public async Task Missing_profile_returns_null_then_save_roundtrips()
    {
        var service = _provider.GetRequiredService<IUserProfileService>();
        Assert.Null(await service.GetAsync("user-1", CancellationToken.None));

        var saved = await service.SaveAsync("user-1", new UserProfileData(
            "Amina Yusuf", "+265991234567", "mw", "Lilongwe", "Area 25"), CancellationToken.None);
        Assert.Equal(UserProfileSaveStatus.Saved, saved.Status);

        var loaded = await service.GetAsync("user-1", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("Amina Yusuf", loaded.DisplayName);
        Assert.Equal("+265991234567", loaded.Phone);
        Assert.Equal("MW", loaded.Country);
        Assert.Equal("Lilongwe", loaded.Region);
        Assert.Equal("Area 25", loaded.Locality);
    }

    [Fact]
    public async Task Save_replaces_existing_profile()
    {
        var service = _provider.GetRequiredService<IUserProfileService>();
        await service.SaveAsync("user-2", new UserProfileData("First", null, null, null, null), CancellationToken.None);
        await service.SaveAsync("user-2", new UserProfileData("Second", "+1 555-0100", "us", null, null), CancellationToken.None);

        var loaded = await service.GetAsync("user-2", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("Second", loaded.DisplayName);
        Assert.Equal("+1 555-0100", loaded.Phone);
        Assert.Equal("US", loaded.Country);
    }

    [Theory]
    [InlineData("", null, null, null, null)]
    [InlineData("   ", null, null, null, null)]
    [InlineData("Name", "not-a-phone", null, null, null)]
    [InlineData("Name", "+12", null, null, null)]
    [InlineData("Name", null, "MWI", null, null)]
    public async Task Invalid_profiles_are_rejected(
        string displayName, string? phone, string? country, string? region, string? locality)
    {
        var service = _provider.GetRequiredService<IUserProfileService>();
        var result = await service.SaveAsync("user-3", new UserProfileData(
            displayName, phone, country, region, locality), CancellationToken.None);
        Assert.Equal(UserProfileSaveStatus.Invalid, result.Status);
        Assert.NotEmpty(result.Errors ?? []);
        Assert.Null(await service.GetAsync("user-3", CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private void CreateTables()
    {
        _dbContext.Database.ExecuteSqlRaw("""
            CREATE TABLE user_profiles (
                user_id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT,
                phone TEXT,
                country TEXT,
                region TEXT,
                locality TEXT,
                updated_at_utc TEXT NOT NULL
            );
            PRAGMA foreign_keys = ON;
            """);
    }

    private sealed class ManualProfileTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
