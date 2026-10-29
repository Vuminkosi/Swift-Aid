using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

public enum EmailVerificationStatus
{
    Sent,
    Throttled,
    Unavailable,
    Verified,
    InvalidCode,
    Expired,
    TooManyAttempts
}

public sealed record EmailVerificationOutcome(EmailVerificationStatus Status);

/// <summary>Delivers account verification codes. Implemented by the SMTP sender.</summary>
public interface IAccountVerificationMailer
{
    Task SendVerificationCodeAsync(string email, string code, CancellationToken cancellationToken);
}

public interface IEmailVerificationService
{
    Task<EmailVerificationOutcome> RequestCodeAsync(SwiftAidUser user, CancellationToken cancellationToken);

    Task<EmailVerificationOutcome> ConfirmCodeAsync(SwiftAidUser user, string code, CancellationToken cancellationToken);

    Task<bool> IsVerifiedAsync(SwiftAidUser user, CancellationToken cancellationToken);
}

/// <summary>
/// Six-digit email verification with hashed storage, short expiry, few attempts
/// and a resend interval. One active code per user.
/// </summary>
public sealed class EmailVerificationService(
    SwiftAidDbContext dbContext,
    UserManager<SwiftAidUser> userManager,
    IPasswordHasher<SwiftAidUser> passwordHasher,
    IAccountVerificationMailer mailer,
    TimeProvider timeProvider) : IEmailVerificationService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(30);
    private const int MaximumAttempts = 5;

    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly UserManager<SwiftAidUser> _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
    private readonly IPasswordHasher<SwiftAidUser> _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
    private readonly IAccountVerificationMailer _mailer = mailer ?? throw new ArgumentNullException(nameof(mailer));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<bool> IsVerifiedAsync(SwiftAidUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var current = await _userManager.FindByIdAsync(user.Id).ConfigureAwait(false);
        return current?.EmailConfirmed == true;
    }

    public async Task<EmailVerificationOutcome> RequestCodeAsync(SwiftAidUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return new EmailVerificationOutcome(EmailVerificationStatus.Unavailable);
        }

        var now = _timeProvider.GetUtcNow();
        var existing = await _dbContext.EmailVerifications
            .FindAsync([user.Id], cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && now - existing.LastSentAtUtc < ResendInterval)
        {
            return new EmailVerificationOutcome(EmailVerificationStatus.Throttled);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        try
        {
            await _mailer.SendVerificationCodeAsync(user.Email, code, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new EmailVerificationOutcome(EmailVerificationStatus.Unavailable);
        }

        var hash = _passwordHasher.HashPassword(user, code);
        if (existing is null)
        {
            _dbContext.EmailVerifications.Add(new EmailVerificationEntity
            {
                UserId = user.Id,
                CodeHash = hash,
                ExpiresAtUtc = now.Add(CodeLifetime),
                AttemptCount = 0,
                LastSentAtUtc = now,
                CreatedAtUtc = now
            });
        }
        else
        {
            existing.CodeHash = hash;
            existing.ExpiresAtUtc = now.Add(CodeLifetime);
            existing.AttemptCount = 0;
            existing.LastSentAtUtc = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new EmailVerificationOutcome(EmailVerificationStatus.Sent);
    }

    public async Task<EmailVerificationOutcome> ConfirmCodeAsync(SwiftAidUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(code))
        {
            return new EmailVerificationOutcome(EmailVerificationStatus.InvalidCode);
        }

        var existing = await _dbContext.EmailVerifications
            .FindAsync([user.Id], cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return new EmailVerificationOutcome(EmailVerificationStatus.Expired);
        }

        var now = _timeProvider.GetUtcNow();
        if (now >= existing.ExpiresAtUtc)
        {
            _dbContext.EmailVerifications.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailVerificationOutcome(EmailVerificationStatus.Expired);
        }

        if (existing.AttemptCount >= MaximumAttempts)
        {
            _dbContext.EmailVerifications.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailVerificationOutcome(EmailVerificationStatus.TooManyAttempts);
        }

        var verification = _passwordHasher.VerifyHashedPassword(user, existing.CodeHash, code.Trim());
        if (verification == PasswordVerificationResult.Failed)
        {
            existing.AttemptCount++;
            var exhausted = existing.AttemptCount >= MaximumAttempts;
            if (exhausted)
            {
                _dbContext.EmailVerifications.Remove(existing);
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailVerificationOutcome(
                exhausted ? EmailVerificationStatus.TooManyAttempts : EmailVerificationStatus.InvalidCode);
        }

        var current = await _userManager.FindByIdAsync(user.Id).ConfigureAwait(false);
        if (current is null)
        {
            return new EmailVerificationOutcome(EmailVerificationStatus.Expired);
        }

        current.EmailConfirmed = true;
        var updated = await _userManager.UpdateAsync(current).ConfigureAwait(false);
        if (!updated.Succeeded)
        {
            return new EmailVerificationOutcome(EmailVerificationStatus.Unavailable);
        }

        _dbContext.EmailVerifications.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new EmailVerificationOutcome(EmailVerificationStatus.Verified);
    }
}
