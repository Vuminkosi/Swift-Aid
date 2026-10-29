using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Exercises the email-change code flow without a network database:
/// request delivery to the new address, duplicate rejection, wrong-code
/// rejection, expiry and the happy path that applies the new address.</summary>
public sealed class EmailChangeServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;
    private readonly ManualTimeProvider _timeProvider;
    private readonly RecordingVerificationMailer _mailer = new();

    public EmailChangeServiceTests()
    {
        _connection.Open();
        _timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite(_connection));
        services.AddIdentityCore<SwiftAidUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<SwiftAidDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.AddScoped<IAccountVerificationMailer>(_ => _mailer);
        services.AddScoped<IEmailVerificationService, EmailVerificationService>();
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
            CREATE TABLE email_verifications (
                user_id TEXT NOT NULL PRIMARY KEY,
                code_hash TEXT NOT NULL,
                pending_email TEXT,
                expires_at_utc TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                last_sent_at_utc TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY (user_id) REFERENCES users (Id) ON DELETE CASCADE
            );
            PRAGMA foreign_keys = ON;
            """);
    }

    [Fact]
    public async Task Request_confirm_happy_path_applies_new_verified_email()
    {
        var user = await CreateUserAsync("change-old@example.invalid");
        var service = Scope();

        var requested = await service.RequestEmailChangeAsync(
            user, "change-new@example.invalid", CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Sent, requested.Status);
        Assert.Single(_mailer.SentTo);
        Assert.Equal("change-new@example.invalid", _mailer.SentTo[0]);

        var confirm = await service.ConfirmEmailChangeAsync(user, _mailer.LastCode, CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Verified, confirm.Status);
        Assert.Equal("change-new@example.invalid", confirm.AppliedEmail);

        var users = _provider.GetRequiredService<UserManager<SwiftAidUser>>();
        var current = await users.FindByIdAsync(user.Id);
        Assert.NotNull(current);
        Assert.Equal("change-new@example.invalid", current.Email);
        Assert.Equal("change-new@example.invalid", current.UserName);
        Assert.True(current.EmailConfirmed);
        Assert.Empty(_dbContext.EmailVerifications);
    }

    [Fact]
    public async Task Same_address_and_taken_address_are_rejected_as_duplicate()
    {
        var user = await CreateUserAsync("change-me@example.invalid");
        await CreateUserAsync("change-taken@example.invalid");
        var service = Scope();

        Assert.Equal(
            EmailVerificationStatus.DuplicateEmail,
            (await service.RequestEmailChangeAsync(user, "change-me@example.invalid", CancellationToken.None)).Status);
        Assert.Equal(
            EmailVerificationStatus.DuplicateEmail,
            (await service.RequestEmailChangeAsync(user, "change-taken@example.invalid", CancellationToken.None)).Status);
        Assert.Empty(_mailer.SentTo);
    }

    [Fact]
    public async Task Second_request_within_interval_is_throttled()
    {
        var user = await CreateUserAsync("change-throttle@example.invalid");
        var service = Scope();

        Assert.Equal(
            EmailVerificationStatus.Sent,
            (await service.RequestEmailChangeAsync(user, "change-a@example.invalid", CancellationToken.None)).Status);
        var throttled = await service.RequestEmailChangeAsync(user, "change-b@example.invalid", CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Throttled, throttled.Status);
        Assert.Single(_mailer.SentTo);

        _timeProvider.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(
            EmailVerificationStatus.Sent,
            (await service.RequestEmailChangeAsync(user, "change-b@example.invalid", CancellationToken.None)).Status);
        Assert.Equal(2, _mailer.SentTo.Count);
    }

    [Fact]
    public async Task Wrong_codes_rejected_then_capped()
    {
        var user = await CreateUserAsync("change-attempts@example.invalid");
        var service = Scope();
        await service.RequestEmailChangeAsync(user, "change-target@example.invalid", CancellationToken.None);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await service.ConfirmEmailChangeAsync(user, "000000", CancellationToken.None);
            Assert.Equal(EmailVerificationStatus.InvalidCode, result.Status);
            Assert.Null(result.AppliedEmail);
        }

        var capped = await service.ConfirmEmailChangeAsync(user, "000000", CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.TooManyAttempts, capped.Status);
    }

    [Fact]
    public async Task Confirm_without_request_is_rejected()
    {
        var user = await CreateUserAsync("change-norequest@example.invalid");
        var service = Scope();

        var result = await service.ConfirmEmailChangeAsync(user, "123456", CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Expired, result.Status);
    }

    [Fact]
    public async Task Expired_code_is_rejected()
    {
        var user = await CreateUserAsync("change-expiry@example.invalid");
        var service = Scope();
        await service.RequestEmailChangeAsync(user, "change-late@example.invalid", CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromMinutes(11));
        var result = await service.ConfirmEmailChangeAsync(user, _mailer.LastCode, CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Expired, result.Status);
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private IEmailVerificationService Scope() => _provider.GetRequiredService<IEmailVerificationService>();

    private async Task<SwiftAidUser> CreateUserAsync(string email)
    {
        var userManager = _provider.GetRequiredService<UserManager<SwiftAidUser>>();
        var user = new SwiftAidUser { Id = Guid.NewGuid().ToString("N"), UserName = email, Email = email };
        Assert.True((await userManager.CreateAsync(user, "Change-Test!9")).Succeeded);
        return user;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan span) => _now = _now.Add(span);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingVerificationMailer : IAccountVerificationMailer
    {
        public List<string> SentTo { get; } = [];

        public string LastCode { get; private set; } = string.Empty;

        public Task SendVerificationCodeAsync(string email, string code, CancellationToken cancellationToken)
        {
            SentTo.Add(email);
            LastCode = code;
            return Task.CompletedTask;
        }
    }
}
