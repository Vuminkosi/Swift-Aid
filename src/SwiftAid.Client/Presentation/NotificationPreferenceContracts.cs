namespace SwiftAid.Client.Presentation;

public sealed record NotificationPreferenceState(
    bool IsSignedIn,
    bool WorkReminders,
    bool SessionUpdates,
    string? StatusMessage = null,
    bool StatusIsError = false);

public sealed record NotificationPreferenceEdit(bool WorkReminders, bool SessionUpdates);

/// <summary>In-app notification preference toggles for the signed-in account.</summary>
public interface INotificationPreferenceClient
{
    Task<UiCapabilityResult<NotificationPreferenceState>> GetAsync(
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<NotificationPreferenceState>> SaveAsync(
        NotificationPreferenceEdit edit,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableNotificationPreferenceClient : INotificationPreferenceClient
{
    public Task<UiCapabilityResult<NotificationPreferenceState>> GetAsync(
        CancellationToken cancellationToken = default) =>
        Unavailable(cancellationToken);

    public Task<UiCapabilityResult<NotificationPreferenceState>> SaveAsync(
        NotificationPreferenceEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Unavailable(cancellationToken);
    }

    private static Task<UiCapabilityResult<NotificationPreferenceState>> Unavailable(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<NotificationPreferenceState>("notification-preferences"));
    }
}
