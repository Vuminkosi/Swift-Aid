using System.Net;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Mobile.Services.Http;

namespace SwiftAid.Mobile.Services.Account;

/// <summary>Notification preferences through the gateway cookie session.</summary>
public sealed class HttpNotificationPreferenceClient(CookieSessionHttpClient session) : INotificationPreferenceClient
{
    private const string Capability = "notification-preferences";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CookieSessionHttpClient _session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<UiCapabilityResult<NotificationPreferenceState>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var response = await _session.GetAsync("/auth/notifications/preferences", cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SignedOut();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable("Notification preferences are temporarily unavailable.");
            }

            var payload = await ReadAsync<NotificationPreferencesPayload>(response, cancellationToken)
                .ConfigureAwait(false);
            return payload is null
                ? Retryable("Notification preferences are temporarily unavailable.")
                : Success(new NotificationPreferenceState(true, payload.WorkReminders, payload.SessionUpdates));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable("Notification preferences are temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<NotificationPreferenceState>> SaveAsync(
        NotificationPreferenceEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PutJsonAsync(
                "/auth/notifications/preferences", edit, token, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SignedOut();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable("Saving preferences is temporarily unavailable.");
            }

            var payload = await ReadAsync<NotificationPreferencesPayload>(response, cancellationToken)
                .ConfigureAwait(false);
            return payload is null
                ? Retryable("Saving preferences is temporarily unavailable.")
                : Success(new NotificationPreferenceState(
                    true, payload.WorkReminders, payload.SessionUpdates, "Preferences saved.", false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable("Saving preferences is temporarily unavailable.");
        }
    }

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static UiCapabilityResult<NotificationPreferenceState> SignedOut() => new(
        UiCapabilityResultCode.Denied,
        new NotificationPreferenceState(false, true, true, "Sign in to view notification preferences.", true),
        Metadata(),
        "Sign in to view notification preferences.");

    private static UiCapabilityResult<NotificationPreferenceState> Success(NotificationPreferenceState state) =>
        new(UiCapabilityResultCode.Success, state, Metadata(), state.StatusMessage);

    private static UiCapabilityResult<NotificationPreferenceState> Retryable(string message) => new(
        UiCapabilityResultCode.Retryable,
        null,
        Metadata(),
        message);

    private static UiCapabilityMetadata Metadata() => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);

    private sealed record NotificationPreferencesPayload(bool WorkReminders, bool SessionUpdates);
}
