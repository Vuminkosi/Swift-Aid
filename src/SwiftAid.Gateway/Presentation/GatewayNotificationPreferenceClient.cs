using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using SwiftAid.Client.Presentation;
using SwiftAid.Infrastructure.Identity;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Serves the signed-in account's notification preferences from the server-side session.</summary>
public sealed class GatewayNotificationPreferenceClient(
    AuthenticationStateProvider authenticationStateProvider,
    UserManager<SwiftAidUser> userManager,
    INotificationPreferenceService preferenceService) : INotificationPreferenceClient
{
    private const string Capability = "notification-preferences";

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly UserManager<SwiftAidUser> _userManager =
        userManager ?? throw new ArgumentNullException(nameof(userManager));
    private readonly INotificationPreferenceService _preferenceService =
        preferenceService ?? throw new ArgumentNullException(nameof(preferenceService));

    public async Task<UiCapabilityResult<NotificationPreferenceState>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var user = await GetUserAsync().ConfigureAwait(false);
            if (user is null)
            {
                return SignedOut();
            }

            var preferences = await _preferenceService.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return Success(new NotificationPreferenceState(true, preferences.WorkReminders, preferences.SessionUpdates));
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
            var user = await GetUserAsync().ConfigureAwait(false);
            if (user is null)
            {
                return SignedOut();
            }

            var saved = await _preferenceService.SaveAsync(
                user.Id,
                new NotificationPreferenceData(edit.WorkReminders, edit.SessionUpdates),
                cancellationToken).ConfigureAwait(false);
            return saved is null
                ? Retryable("Saving preferences is temporarily unavailable.")
                : Success(new NotificationPreferenceState(
                    true, saved.WorkReminders, saved.SessionUpdates, "Preferences saved.", false));
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

    private async Task<SwiftAidUser?> GetUserAsync()
    {
        var authentication = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return authentication.User.Identity?.IsAuthenticated == true
            ? await _userManager.GetUserAsync(authentication.User).ConfigureAwait(false)
            : null;
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
}
