using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Exercises profile photo validation, storage mapping and removal
/// without a network database or object store.</summary>
public sealed class ProfileAvatarServiceTests : IAsyncDisposable
{
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    ];

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;
    private readonly RecordingObjectStorage _storage = new();

    public ProfileAvatarServiceTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite(_connection));
        services.AddIdentityCore<SwiftAidUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<SwiftAidDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.AddSingleton<IObjectStorage>(_storage);
        services.AddScoped<IProfileAvatarService, ProfileAvatarService>();
        _provider = services.BuildServiceProvider();
        _dbContext = _provider.GetRequiredService<SwiftAidDbContext>();
        CreateTables();
    }

    private void CreateTables()
    {
        _dbContext.Database.ExecuteSqlRaw("""
            CREATE TABLE users (
                Id TEXT NOT NULL PRIMARY KEY,
                UserName TEXT,
                NormalizedUserName TEXT,
                Email TEXT,
                NormalizedEmail TEXT,
                EmailConfirmed INTEGER NOT NULL,
                PasswordHash TEXT,
                SecurityStamp TEXT,
                ConcurrencyStamp TEXT,
                PhoneNumber TEXT,
                PhoneNumberConfirmed INTEGER NOT NULL,
                TwoFactorEnabled INTEGER NOT NULL,
                LockoutEnd TEXT,
                LockoutEnabled INTEGER NOT NULL,
                AccessFailedCount INTEGER NOT NULL
            );
            CREATE TABLE user_profiles (
                user_id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT,
                phone TEXT,
                country TEXT,
                region TEXT,
                locality TEXT,
                avatar_object_id TEXT,
                avatar_content_type TEXT,
                updated_at_utc TEXT NOT NULL,
                FOREIGN KEY (user_id) REFERENCES users (Id) ON DELETE CASCADE
            );
            PRAGMA foreign_keys = ON;
            """);
    }

    [Fact]
    public async Task Save_and_read_roundtrip_preserves_bytes_and_type()
    {
        var user = await CreateUserAsync("avatar-happy@example.invalid");
        var service = Scope();

        using var upload = new MemoryStream(PngBytes);
        var saved = await service.SaveAsync(user.Id, "image/png", upload, CancellationToken.None);
        Assert.Equal(ProfileAvatarStatus.Saved, saved.Status);
        Assert.Equal("image/png", saved.ContentType);
        Assert.Single(_storage.Stored);

        var (result, data) = await service.ReadAsync(user.Id, CancellationToken.None);
        Assert.Equal(ProfileAvatarStatus.Saved, result.Status);
        Assert.NotNull(data);
        Assert.Equal("image/png", data.ContentType);
        Assert.Equal(PngBytes, data.Content);
    }

    [Fact]
    public async Task Unsupported_declared_type_is_rejected_before_storage()
    {
        var user = await CreateUserAsync("avatar-type@example.invalid");
        var service = Scope();

        using var upload = new MemoryStream(PngBytes);
        var saved = await service.SaveAsync(user.Id, "image/gif", upload, CancellationToken.None);
        Assert.Equal(ProfileAvatarStatus.UnsupportedType, saved.Status);
        Assert.Empty(_storage.Stored);
    }

    [Fact]
    public async Task Mismatched_magic_bytes_are_rejected()
    {
        var user = await CreateUserAsync("avatar-magic@example.invalid");
        var service = Scope();

        using var upload = new MemoryStream(JpegBytes);
        var saved = await service.SaveAsync(user.Id, "image/png", upload, CancellationToken.None);
        Assert.Equal(ProfileAvatarStatus.UnsupportedType, saved.Status);
        Assert.Empty(_storage.Stored);
    }

    [Fact]
    public async Task Oversize_upload_is_rejected()
    {
        var user = await CreateUserAsync("avatar-large@example.invalid");
        var service = Scope();
        var large = new byte[ProfileAvatarService.MaxAvatarBytes + 16];
        large[0] = 0xFF;
        large[1] = 0xD8;
        large[2] = 0xFF;

        using var upload = new MemoryStream(large);
        var saved = await service.SaveAsync(user.Id, "image/jpeg", upload, CancellationToken.None);
        Assert.Equal(ProfileAvatarStatus.TooLarge, saved.Status);
        Assert.Empty(_storage.Stored);
    }

    [Fact]
    public async Task Unconfigured_storage_maps_to_not_configured()
    {
        var user = await CreateUserAsync("avatar-off@example.invalid");
        _storage.Mode = RecordingObjectStorage.Behavior.NotConfigured;
        var service = Scope();

        using var upload = new MemoryStream(PngBytes);
        var saved = await service.SaveAsync(user.Id, "image/png", upload, CancellationToken.None);
        Assert.Equal(ProfileAvatarStatus.NotConfigured, saved.Status);
    }

    [Fact]
    public async Task Read_without_photo_is_not_found()
    {
        var user = await CreateUserAsync("avatar-missing@example.invalid");
        var service = Scope();

        var (result, data) = await service.ReadAsync(user.Id, CancellationToken.None);
        Assert.Equal(ProfileAvatarStatus.NotFound, result.Status);
        Assert.Null(data);
    }

    [Fact]
    public async Task Delete_clears_reference_and_read_goes_not_found()
    {
        var user = await CreateUserAsync("avatar-delete@example.invalid");
        var service = Scope();

        using var upload = new MemoryStream(PngBytes);
        Assert.Equal(
            ProfileAvatarStatus.Saved,
            (await service.SaveAsync(user.Id, "image/png", upload, CancellationToken.None)).Status);

        Assert.Equal(
            ProfileAvatarStatus.Deleted,
            (await service.DeleteAsync(user.Id, CancellationToken.None)).Status);

        var (result, data) = await service.ReadAsync(user.Id, CancellationToken.None);
        Assert.Equal(ProfileAvatarStatus.NotFound, result.Status);
        Assert.Null(data);
    }

    [Fact]
    public async Task Delete_without_photo_is_not_found()
    {
        var user = await CreateUserAsync("avatar-nodelete@example.invalid");
        var service = Scope();

        Assert.Equal(
            ProfileAvatarStatus.NotFound,
            (await service.DeleteAsync(user.Id, CancellationToken.None)).Status);
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private IProfileAvatarService Scope() => _provider.GetRequiredService<IProfileAvatarService>();

    private async Task<SwiftAidUser> CreateUserAsync(string email)
    {
        var userManager = _provider.GetRequiredService<UserManager<SwiftAidUser>>();
        var user = new SwiftAidUser { Id = Guid.NewGuid().ToString("N"), UserName = email, Email = email };
        Assert.True((await userManager.CreateAsync(user, "Avatar-Test!9")).Succeeded);
        return user;
    }

    private sealed class RecordingObjectStorage : IObjectStorage
    {
        public enum Behavior
        {
            Succeed,
            NotConfigured,
            Fail
        }

        public Behavior Mode { get; set; } = Behavior.Succeed;

        public List<(Guid Id, string ContentType, byte[] Content)> Stored { get; } = [];

        public ValueTask<ObjectStorageResult> StoreAsync(
            Stream content,
            string contentType,
            CancellationToken cancellationToken)
        {
            return Mode switch
            {
                Behavior.NotConfigured => ValueTask.FromResult(
                    new ObjectStorageResult(ObjectStorageStatus.NotConfigured)),
                Behavior.Fail => ValueTask.FromResult(
                    new ObjectStorageResult(ObjectStorageStatus.Failed, FailureCode: "boom")),
                _ => StoreCoreAsync(content, contentType, cancellationToken)
            };
        }

        public ValueTask<ObjectStorageResult> CopyToAsync(
            ObjectReference reference,
            Stream destination,
            CancellationToken cancellationToken)
        {
            var stored = Stored.FirstOrDefault(entry => entry.Id == reference.Id);
            if (stored == default)
            {
                return ValueTask.FromResult(new ObjectStorageResult(ObjectStorageStatus.NotFound));
            }

            destination.Write(stored.Content, 0, stored.Content.Length);
            return ValueTask.FromResult(new ObjectStorageResult(ObjectStorageStatus.Succeeded, reference));
        }

        private async ValueTask<ObjectStorageResult> StoreCoreAsync(
            Stream content,
            string contentType,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var reference = new ObjectReference(Guid.NewGuid());
            Stored.Add((reference.Id, contentType, buffer.ToArray()));
            return new ObjectStorageResult(ObjectStorageStatus.Succeeded, reference);
        }
    }
}
