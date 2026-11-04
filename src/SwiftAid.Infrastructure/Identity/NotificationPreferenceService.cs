using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

public sealed record NotificationPreferenceData(bool WorkReminders, bool SessionUpdates);

public interface INotificationPreferenceService
{
    Task<NotificationPreferenceData> GetAsync(string userId, CancellationToken cancellationToken);

    Task<NotificationPreferenceData?> SaveAsync(
        string userId,
        NotificationPreferenceData preferences,
        CancellationToken cancellationToken);
}

/// <summary>In-app preference toggles. Defaults keep both reminders on for new accounts.</summary>
public sealed class NotificationPreferenceService(
    SwiftAidDbContext dbContext,
    TimeProvider timeProvider) : INotificationPreferenceService
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<NotificationPreferenceData> GetAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new NotificationPreferenceData(WorkReminders: true, SessionUpdates: true);
        }

        var entity = await _dbContext.NotificationPreferences
            .FindAsync([userId], cancellationToken)
            .ConfigureAwait(false);
        return entity is null
            ? new NotificationPreferenceData(WorkReminders: true, SessionUpdates: true)
            : new NotificationPreferenceData(entity.WorkReminders, entity.SessionUpdates);
    }

    public async Task<NotificationPreferenceData?> SaveAsync(
        string userId,
        NotificationPreferenceData preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            var entity = await _dbContext.NotificationPreferences
                .FindAsync([userId], cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                _dbContext.NotificationPreferences.Add(new NotificationPreferenceEntity
                {
                    UserId = userId,
                    WorkReminders = preferences.WorkReminders,
                    SessionUpdates = preferences.SessionUpdates,
                    UpdatedAtUtc = now
                });
            }
            else
            {
                entity.WorkReminders = preferences.WorkReminders;
                entity.SessionUpdates = preferences.SessionUpdates;
                entity.UpdatedAtUtc = now;
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return preferences;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
