using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

public enum UserProfileSaveStatus
{
    Saved,
    Invalid,
    Unavailable
}

public sealed record UserProfileData(
    string? DisplayName,
    string? Phone,
    string? Country,
    string? Region,
    string? Locality);

public sealed record UserProfileSaveResult(UserProfileSaveStatus Status, IReadOnlyList<string>? Errors = null);

public interface IUserProfileService
{
    Task<UserProfileData?> GetAsync(string userId, CancellationToken cancellationToken);

    Task<UserProfileSaveResult> SaveAsync(string userId, UserProfileData profile, CancellationToken cancellationToken);
}

/// <summary>Persists self-asserted profile fields. Email and membership stay with their owners.</summary>
public sealed class UserProfileService(SwiftAidDbContext dbContext, TimeProvider timeProvider) : IUserProfileService
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<UserProfileData?> GetAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var entity = await _dbContext.UserProfiles
            .FindAsync([userId], cancellationToken)
            .ConfigureAwait(false);
        return entity is null
            ? null
            : new UserProfileData(
                entity.DisplayName,
                entity.Phone,
                entity.Country,
                entity.Region,
                entity.Locality);
    }

    public async Task<UserProfileSaveResult> SaveAsync(
        string userId,
        UserProfileData profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = Validate(profile);
        if (errors.Count > 0 || string.IsNullOrWhiteSpace(userId))
        {
            return new UserProfileSaveResult(UserProfileSaveStatus.Invalid, errors);
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            var entity = await _dbContext.UserProfiles
                .FindAsync([userId], cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                _dbContext.UserProfiles.Add(new UserProfileEntity
                {
                    UserId = userId,
                    DisplayName = profile.DisplayName!.Trim(),
                    Phone = NormalizeOptional(profile.Phone),
                    Country = NormalizeOptional(profile.Country)?.ToUpperInvariant(),
                    Region = NormalizeOptional(profile.Region),
                    Locality = NormalizeOptional(profile.Locality),
                    UpdatedAtUtc = now
                });
            }
            else
            {
                entity.DisplayName = profile.DisplayName!.Trim();
                entity.Phone = NormalizeOptional(profile.Phone);
                entity.Country = NormalizeOptional(profile.Country)?.ToUpperInvariant();
                entity.Region = NormalizeOptional(profile.Region);
                entity.Locality = NormalizeOptional(profile.Locality);
                entity.UpdatedAtUtc = now;
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new UserProfileSaveResult(UserProfileSaveStatus.Saved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new UserProfileSaveResult(UserProfileSaveStatus.Unavailable);
        }
    }

    private static List<string> Validate(UserProfileData profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Trim().Length > 128)
        {
            errors.Add("Enter a display name up to 128 characters.");
        }

        if (!string.IsNullOrWhiteSpace(profile.Phone) && !IsValidPhone(profile.Phone.Trim()))
        {
            errors.Add("Enter a valid phone number starting with + and your country code.");
        }

        if (!string.IsNullOrWhiteSpace(profile.Country) &&
            (profile.Country.Trim().Length != 2 || !profile.Country.Trim().All(char.IsLetter)))
        {
            errors.Add("Country uses a two-letter code.");
        }

        if (profile.Region?.Trim().Length > 128)
        {
            errors.Add("Region is limited to 128 characters.");
        }

        if (profile.Locality?.Trim().Length > 128)
        {
            errors.Add("Locality is limited to 128 characters.");
        }

        return errors;
    }

    private static bool IsValidPhone(string phone) =>
        phone.Length is >= 7 and <= 24 &&
        phone[0] == '+' &&
        phone.Skip(1).All(character =>
            char.IsDigit(character) || character is ' ' or '-' or '(' or ')');

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
