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
    IEmailVerificationService verificationService,
    IProfileAvatarService avatarService,
    SignInManager<SwiftAidUser> signInManager) : IUserProfileClient
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
    private readonly IProfileAvatarService _avatarService =
        avatarService ?? throw new ArgumentNullException(nameof(avatarService));
    private readonly SignInManager<SwiftAidUser> _signInManager =
        signInManager ?? throw new ArgumentNullException(nameof(signInManager));

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

    public async Task<UiCapabilityResult<UserProfileState>> UploadAvatarAsync(
        UserProfileAvatarUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var user = await GetUserAsync().ConfigureAwait(false);
            if (user is null)
            {
                return SignedOut();
            }

            var saved = await _avatarService.SaveAsync(
                user.Id, upload.ContentType, upload.Content, cancellationToken).ConfigureAwait(false);
            return saved.Status switch
            {
                ProfileAvatarStatus.Saved => Success(
                    await StateAsync(user, "Photo saved.", cancellationToken).ConfigureAwait(false)),
                ProfileAvatarStatus.UnsupportedType or ProfileAvatarStatus.TooLarge or ProfileAvatarStatus.Invalid =>
                    Invalid(await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                        "Use a JPEG, PNG, or WebP photo up to 2 MB."),
                ProfileAvatarStatus.NotConfigured => Retryable("Photo uploads are not available right now."),
                _ => Retryable("Saving your photo is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable("Saving your photo is temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<UserProfileState>> DeleteAvatarAsync(
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

            var deleted = await _avatarService.DeleteAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return deleted.Status switch
            {
                ProfileAvatarStatus.Deleted => Success(
                    await StateAsync(user, "Photo removed.", cancellationToken).ConfigureAwait(false)),
                ProfileAvatarStatus.NotFound => Invalid(
                    await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                    "No photo is set for this account."),
                _ => Retryable("Removing your photo is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable("Removing your photo is temporarily unavailable.");
        }
    }

    public async Task<UiCapabilityResult<UserProfileState>> RequestEmailChangeAsync(
        string newEmail,
        string currentPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newEmail) || string.IsNullOrWhiteSpace(currentPassword))
        {
            return Invalid(
                await SignedOutStateAsync(cancellationToken).ConfigureAwait(false),
                "Enter a new email address and your current password.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var user = await GetUserAsync().ConfigureAwait(false);
            if (user is null)
            {
                return SignedOut();
            }

            var check = await _signInManager.CheckPasswordSignInAsync(user, currentPassword, lockoutOnFailure: true)
                .ConfigureAwait(false);
            if (!check.Succeeded)
            {
                return Invalid(
                    await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                    "The current password is incorrect.");
            }

            var outcome = await _verificationService.RequestEmailChangeAsync(
                user, newEmail.Trim(), cancellationToken).ConfigureAwait(false);
            return outcome.Status switch
            {
                EmailVerificationStatus.Sent => Success(await StateAsync(
                    user,
                    "A verification code has been sent to your new email address.",
                    cancellationToken).ConfigureAwait(false)),
                EmailVerificationStatus.Throttled => Invalid(
                    await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                    "A code was sent recently. Wait before requesting a new one."),
                EmailVerificationStatus.DuplicateEmail => Invalid(
                    await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                    "This email address is already associated with another account."),
                _ => Retryable("Email changes are temporarily unavailable. Try again later.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Retryable(ClientRequestFailure.MessageFor(ex, "Email change"));
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            return Retryable(ClientRequestFailure.MessageFor(ex, "Email change"));
        }
        catch (Exception)
        {
            return Retryable("Email changes are temporarily unavailable. Try again later.");
        }
    }

    public async Task<UiCapabilityResult<UserProfileState>> ConfirmEmailChangeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Invalid(
                await SignedOutStateAsync(cancellationToken).ConfigureAwait(false),
                "Enter the verification code.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var user = await GetUserAsync().ConfigureAwait(false);
            if (user is null)
            {
                return SignedOut();
            }

            var outcome = await _verificationService.ConfirmEmailChangeAsync(
                user, code.Trim(), cancellationToken).ConfigureAwait(false);
            return outcome.Status switch
            {
                EmailVerificationStatus.Verified => Success(await StateAsync(
                    user,
                    "Your email address has been updated.",
                    cancellationToken).ConfigureAwait(false)),
                EmailVerificationStatus.InvalidCode => Invalid(
                    await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                    "The code is incorrect. Check the latest email and try again."),
                EmailVerificationStatus.TooManyAttempts => Invalid(
                    await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                    "Too many incorrect attempts. Request a new code."),
                EmailVerificationStatus.DuplicateEmail => Invalid(
                    await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                    "This email address is already associated with another account."),
                EmailVerificationStatus.Expired => Invalid(
                    await StateAsync(user, null, cancellationToken).ConfigureAwait(false),
                    "The code is invalid or has expired. Request a new one."),
                _ => Retryable("Email changes are temporarily unavailable. Try again later.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Retryable(ClientRequestFailure.MessageFor(ex, "Email change"));
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            return Retryable(ClientRequestFailure.MessageFor(ex, "Email change"));
        }
        catch (Exception)
        {
            return Retryable("Email changes are temporarily unavailable. Try again later.");
        }
    }

    private async Task<UserProfileState> SignedOutStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await GetUserAsync().ConfigureAwait(false);
        return user is null
            ? new UserProfileState(false, null, null, null, null, null, null, false)
            : await StateAsync(user, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserProfileState> StateAsync(
        SwiftAidUser user,
        string? status,
        CancellationToken cancellationToken)
    {
        var profile = await _profileService.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var verified = await _verificationService.IsVerifiedAsync(user, cancellationToken).ConfigureAwait(false);
        var hasAvatar = await _avatarService.HasAvatarAsync(user.Id, cancellationToken).ConfigureAwait(false);
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
            StatusIsError: false,
            AvatarUrl: hasAvatar ? "/auth/profile/avatar" : null);
    }

    private static UiCapabilityResult<UserProfileState> Invalid(UserProfileState state, string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        state with { StatusMessage = message, StatusIsError = true },
        Metadata(),
        message);

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
