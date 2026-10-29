using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

public enum PasswordRegistrationStatus
{
    Registered,
    DuplicateEmail,
    InvalidPassword,
    Unavailable
}

public sealed record PasswordRegistrationResult(
    PasswordRegistrationStatus Status,
    string? ActorId = null,
    IReadOnlyList<string>? Errors = null);

public interface IPasswordIdentityRegistrar
{
    Task<PasswordRegistrationResult> RegisterAsync(
        string email,
        string password,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates the ordinary actor, its access state, and the password-backed Identity
/// user atomically. Mirrors the external-identity actor bootstrap so password
/// accounts satisfy the same authority constraints.
/// </summary>
public sealed class PasswordIdentityRegistrar(
    SwiftAidDbContext dbContext,
    UserManager<SwiftAidUser> userManager,
    TimeProvider timeProvider) : IPasswordIdentityRegistrar
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly UserManager<SwiftAidUser> _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<PasswordRegistrationResult> RegisterAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
            string.IsNullOrEmpty(password) || password.Length > 256)
        {
            return new PasswordRegistrationResult(PasswordRegistrationStatus.InvalidPassword);
        }

        var existing = await _userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (existing is not null)
        {
            return new PasswordRegistrationResult(PasswordRegistrationStatus.DuplicateEmail);
        }

        var tracked = _dbContext.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            existing = await _userManager.FindByEmailAsync(email).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                DetachAttemptEntries(tracked);
                return new PasswordRegistrationResult(PasswordRegistrationStatus.DuplicateEmail);
            }

            var now = _timeProvider.GetUtcNow();
            var actorId = Guid.NewGuid().ToString("N");
            _dbContext.Actors.Add(new ActorEntity
            {
                ActorId = actorId,
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            _dbContext.ActorAccessStates.Add(new ActorAccessStateEntity
            {
                ActorId = actorId,
                AccessEpoch = 0,
                Version = 1,
                UpdatedAtUtc = now
            });

            var user = new SwiftAidUser
            {
                Id = actorId,
                UserName = email,
                Email = email,
                EmailConfirmed = false,
                LockoutEnabled = true
            };
            var created = await _userManager.CreateAsync(user, password).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                var errors = created.Errors
                    .Select(error => error.Description)
                    .Where(description => !string.IsNullOrWhiteSpace(description))
                    .ToList();
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                DetachAttemptEntries(tracked);
                if (created.Errors.Any(error =>
                        string.Equals(error.Code, "DuplicateUserName", StringComparison.Ordinal)))
                {
                    return new PasswordRegistrationResult(PasswordRegistrationStatus.DuplicateEmail);
                }

                return new PasswordRegistrationResult(PasswordRegistrationStatus.InvalidPassword, Errors: errors);
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PasswordRegistrationResult(PasswordRegistrationStatus.Registered, actorId);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            DetachAttemptEntries(tracked);
            throw;
        }
    }

    private void DetachAttemptEntries(HashSet<object> tracked)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries().ToArray())
        {
            if (!tracked.Contains(entry.Entity))
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}
