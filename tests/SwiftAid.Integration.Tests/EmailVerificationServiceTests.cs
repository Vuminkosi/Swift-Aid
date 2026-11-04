using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Exercises the email OTP rules without a network database: resend throttle,
/// wrong-code rejection, attempt cap, expiry and the happy path that confirms the email.</summary>
public sealed class EmailVerificationServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;
    private readonly ManualTimeProvider _timeProvider;
    private readonly RecordingVerificationMailer _mailer = new();

    public EmailVerificationServiceTests()
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
        CreateIdentityTables();
    }

    private void CreateIdentityTables()
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
            CREATE UNIQUE INDEX UserNameIndex ON users (NormalizedUserName);
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
    public async Task Request_send_confirm_happy_path_marks_email_confirmed()
    {
        var user = await CreateUserAsync("otp-happy@example.invalid");
        var service = Scope();

        var requested = await service.RequestCodeAsync(user, CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Sent, requested.Status);
        Assert.Single(_mailer.SentTo);

        var confirm = await service.ConfirmCodeAsync(user, _mailer.LastCode, CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Verified, confirm.Status);
        Assert.True(await service.IsVerifiedAsync(user, CancellationToken.None));
        Assert.Empty(_dbContext.EmailVerifications);
    }

    [Fact]
    public async Task Request_twice_within_interval_is_throttled_without_resend()
    {
        var user = await CreateUserAsync("otp-throttle@example.invalid");
        var service = Scope();

        var first = await service.RequestCodeAsync(user, CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Sent, first.Status);
        Assert.Equal(30, first.RetryAfterSeconds);
        var throttled = await service.RequestCodeAsync(user, CancellationToken.None);
        Assert.Equal(EmailVerificationStatus.Throttled, throttled.Status);
        Assert.InRange(throttled.RetryAfterSeconds, 1, 30);
        Assert.Single(_mailer.SentTo);

        _timeProvider.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(EmailVerificationStatus.Sent, (await service.RequestCodeAsync(user, CancellationToken.None)).Status);
        Assert.Equal(2, _mailer.SentTo.Count);
    }

    [Fact]
    public async Task Wrong_codes_rejected_then_capped_after_five_attempts()
    {
        var user = await CreateUserAsync("otp-attempts@example.invalid");
        var service = Scope();
        await service.RequestCodeAsync(user, CancellationToken.None);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(
                EmailVerificationStatus.InvalidCode,
                (await service.ConfirmCodeAsync(user, "000000", CancellationToken.None)).Status);
        }

        Assert.Equal(
            EmailVerificationStatus.TooManyAttempts,
            (await service.ConfirmCodeAsync(user, "000000", CancellationToken.None)).Status);
        Assert.False(await service.IsVerifiedAsync(user, CancellationToken.None));
        Assert.Equal(
            EmailVerificationStatus.Expired,
            (await service.ConfirmCodeAsync(user, _mailer.LastCode, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Expired_code_is_rejected()
    {
        var user = await CreateUserAsync("otp-expiry@example.invalid");
        var service = Scope();
        await service.RequestCodeAsync(user, CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(
            EmailVerificationStatus.Expired,
            (await service.ConfirmCodeAsync(user, _mailer.LastCode, CancellationToken.None)).Status);
        Assert.False(await service.IsVerifiedAsync(user, CancellationToken.None));
    }

    [Fact]
    public async Task Confirm_without_request_is_rejected()
    {
        var user = await CreateUserAsync("otp-norequest@example.invalid");
        var service = Scope();
        Assert.Equal(
            EmailVerificationStatus.Expired,
            (await service.ConfirmCodeAsync(user, "123456", CancellationToken.None)).Status);
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
        Assert.True((await userManager.CreateAsync(user, "Otp-Test!9")).Succeeded);
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
