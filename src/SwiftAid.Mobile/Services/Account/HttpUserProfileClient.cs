using System.Net;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Mobile.Services.Http;

namespace SwiftAid.Mobile.Services.Account;

/// <summary>Loads and saves the signed-in profile through the gateway cookie session.</summary>
public sealed class HttpUserProfileClient(CookieSessionHttpClient session) : IUserProfileClient
{
    private const string Capability = "user-profile";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CookieSessionHttpClient _session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<UiCapabilityResult<UserProfileState>> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var response = await _session.GetAsync("/auth/profile", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SignedOut();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable("Your profile is temporarily unavailable.");
            }

            var profile = await ReadProfileAsync(response, cancellationToken).ConfigureAwait(false);
            return profile is null
                ? Retryable("Your profile is temporarily unavailable.")
                : Success(ToState(profile, null));
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
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PutJsonAsync("/auth/profile", edit, token, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SignedOut();
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var message = await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? "Check your profile details and try again.";
                var current = await GetAsync(cancellationToken).ConfigureAwait(false);
                var state = (current.Value ?? EmptySignedIn()) with { StatusMessage = message, StatusIsError = true };
                return new UiCapabilityResult<UserProfileState>(
                    UiCapabilityResultCode.InvalidRequest,
                    state,
                    Metadata(),
                    message);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable("Saving your profile is temporarily unavailable.");
            }

            var profile = await ReadProfileAsync(response, cancellationToken).ConfigureAwait(false);
            return profile is null
                ? Retryable("Saving your profile is temporarily unavailable.")
                : Success(ToState(profile, "Profile saved."));
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

    public async Task<UiCapabilityResult<UserProfileState>> UploadAvatarAsync(
        UserProfileAvatarUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var fileContent = new StreamContent(upload.Content);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(upload.ContentType);
            using var form = new MultipartFormDataContent
            {
                { fileContent, "avatar", "avatar" }
            };
            using var response = await _session.PutFormAsync("/auth/profile/avatar", form, token, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SignedOut();
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return await InvalidStateAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? "Saving your photo is temporarily unavailable.";
                return Retryable(message);
            }

            var current = await GetAsync(cancellationToken).ConfigureAwait(false);
            var state = (current.Value ?? EmptySignedIn()) with
            {
                StatusMessage = "Photo saved.",
                StatusIsError = false
            };
            return Success(state);
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
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.DeleteAsync("/auth/profile/avatar", token, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SignedOut();
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return await InvalidStateAsync(cancellationToken, "No photo is set for this account.")
                    .ConfigureAwait(false);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return await InvalidStateAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? "Removing your photo is temporarily unavailable.";
                return Retryable(message);
            }

            var current = await GetAsync(cancellationToken).ConfigureAwait(false);
            var state = (current.Value ?? EmptySignedIn()) with
            {
                StatusMessage = "Photo removed.",
                StatusIsError = false
            };
            return Success(state);
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

    private async Task<UiCapabilityResult<UserProfileState>> InvalidStateAsync(
        CancellationToken cancellationToken,
        string? fallback = null)
    {
        var message = fallback ?? "Use a JPEG, PNG, or WebP photo up to 2 MB.";
        var current = await GetAsync(cancellationToken).ConfigureAwait(false);
        var state = (current.Value ?? EmptySignedIn()) with { StatusMessage = message, StatusIsError = true };
        return new UiCapabilityResult<UserProfileState>(
            UiCapabilityResultCode.InvalidRequest,
            state,
            Metadata(),
            message);
    }

    private static UserProfileState EmptySignedIn() =>
        new(true, null, null, null, null, null, null, false);

    private static async Task<UserProfilePayload?> ReadProfileAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<UserProfilePayload>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private UserProfileState ToState(UserProfilePayload payload, string? status)
    {
        var baseAddress = _session.BaseAddress?.TrimEnd('/');
        return new UserProfileState(
            true,
            payload.DisplayName,
            payload.Phone,
            payload.Country,
            payload.Region,
            payload.Locality,
            payload.Email,
            payload.EmailVerified,
            status,
            StatusIsError: false,
            AvatarUrl: payload.HasAvatar
                ? (baseAddress is null ? "/auth/profile/avatar" : baseAddress + "/auth/profile/avatar")
                : null);
    }

    private sealed record UserProfilePayload(
        string? DisplayName,
        string? Phone,
        string? Country,
        string? Region,
        string? Locality,
        string? Email,
        bool EmailVerified,
        bool HasAvatar);
}
