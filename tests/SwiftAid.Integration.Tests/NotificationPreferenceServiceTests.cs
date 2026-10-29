using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Exercises notification preference defaults and persistence.</summary>
public sealed class NotificationPreferenceServiceTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;

    public NotificationPreferenceServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();
        _provider = services.BuildServiceProvider();
        _dbContext = _provider.GetRequiredService<SwiftAidDbContext>();
        _dbContext.Database.OpenConnection();
        _dbContext.Database.ExecuteSqlRaw("""
            CREATE TABLE notification_preferences (
                user_id TEXT NOT NULL PRIMARY KEY,
                work_reminders INTEGER NOT NULL,
                session_updates INTEGER NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            """);
    }

    [Fact]
    public async Task Defaults_keep_both_reminders_on()
    {
        var service = _provider.GetRequiredService<INotificationPreferenceService>();
        var defaults = await service.GetAsync("user-1", CancellationToken.None);

        Assert.True(defaults.WorkReminders);
        Assert.True(defaults.SessionUpdates);
    }

    [Fact]
    public async Task Save_roundtrips_toggles()
    {
        var service = _provider.GetRequiredService<INotificationPreferenceService>();

        var saved = await service.SaveAsync("user-1", new NotificationPreferenceData(false, true), CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(saved.WorkReminders);
        Assert.True(saved.SessionUpdates);

        var loaded = await service.GetAsync("user-1", CancellationToken.None);
        Assert.False(loaded.WorkReminders);
        Assert.True(loaded.SessionUpdates);
    }

    [Fact]
    public async Task Save_rejects_missing_user()
    {
        var service = _provider.GetRequiredService<INotificationPreferenceService>();
        Assert.Null(await service.SaveAsync("  ", new NotificationPreferenceData(false, false), CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
