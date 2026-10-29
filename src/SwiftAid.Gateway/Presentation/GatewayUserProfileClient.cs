using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using SwiftAid.Client.Presentation;
using SwiftAid.Infrastructure.Identity;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Serves the signed-in account's profile from the server-side session.</summary>
public sealed class GatewayUserProfileClient(
    AuthenticationStateProvider authenticationStateProvider,
    UserManager<SwiftAidUser> userManager,
    IUserProfileService profileService,
    IEmailVerificationService verificationService) : IUserProfileClient
{
    private const string Capability = "user-profile";

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly UserManager<SwiftAidUser> _userManager =
        userManager ?? throw new ArgumentNullException(nameof(userManager));
    private readonly IUserProfileService _profileService =
        profileService ?? throw new ArgumentNullException(nameof(profileService));
    private readonly IEmailVerificationService _verificationService =
        verificationService ?? throw new ArgumentNullException(nameof(verificationService));

    public async Task<UiCapabilityResult<UserProfileState>> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var user = await GetUserAsync().ConfigureAwait(false);
            if (user is null)
            {
                return SignedOut();
            }

            return Success(await StateAsync(user, null, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable("Your profile is temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<UserProfileState>> SaveAsync(
        UserProfileEdit edit,
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

            var saved = await _profileService.SaveAsync(
                user.Id,
                new UserProfileData(edit.DisplayName, edit.Phone, edit.Country, edit.Region, edit.Locality),
                cancellationToken).ConfigureAwait(false);
            if (saved.Status == UserProfileSaveStatus.Invalid)
            {
                var state = await StateAsync(user, saved.Errors?.FirstOrDefault(), cancellationToken).ConfigureAwait(false);
                return new UiCapabilityResult<UserProfileState>(
                    UiCapabilityResultCode.InvalidRequest,
                    state with { StatusIsError = true },
                    Metadata(),
                    state.StatusMessage);
            }

            if (saved.Status != UserProfileSaveStatus.Saved)
            {
                return Retryable("Saving your profile is temporarily unavailable.");
            }

            return Success(await StateAsync(user, "Profile saved.", cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable("Saving your profile is temporarily unavailable.");
        }
    }

    private async Task<SwiftAidUser?> GetUserAsync()
    {
        var authentication = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return authentication.User.Identity?.IsAuthenticated == true
            ? await _userManager.GetUserAsync(authentication.User).ConfigureAwait(false)
            : null;
    }

    private async Task<UserProfileState> StateAsync(
        SwiftAidUser user,
        string? status,
        CancellationToken cancellationToken)
    {
        var profile = await _profileService.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var verified = await _verificationService.IsVerifiedAsync(user, cancellationToken).ConfigureAwait(false);
        return new UserProfileState(
            true,
            profile?.DisplayName,
            profile?.Phone,
            profile?.Country,
            profile?.Region,
            profile?.Locality,
            user.Email,
            verified,
            status,
            StatusIsError: false);
    }

    private static UiCapabilityResult<UserProfileState> SignedOut() => new(
        UiCapabilityResultCode.Denied,
        new UserProfileState(false, null, null, null, null, null, null, false, "Sign in to view your profile.", true),
        Metadata(),
        "Sign in to view your profile.");

    private static UiCapabilityResult<UserProfileState> Success(UserProfileState state) =>
        new(UiCapabilityResultCode.Success, state, Metadata(), state.StatusMessage);

    private static UiCapabilityResult<UserProfileState> Retryable(string message) => new(
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
