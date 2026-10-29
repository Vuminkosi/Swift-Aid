using System.Net;
using System.Text.Json;
using SwiftAid.Mobile.Services.Http;

namespace SwiftAid.Mobile.Services.Account;

public enum PasswordAuthStatus
{
    Succeeded,
    InvalidCredentials,
    DuplicateEmail,
    InvalidRequest,
    Throttled,
    Unavailable,
    SignedOut
}

public sealed record PasswordAuthOutcome(PasswordAuthStatus Status, string? Email = null, string? Message = null);

public sealed record PasswordPolicyInfo(
    int MinimumLength,
    bool RequireDigit,
    bool RequireLowercase,
    bool RequireUppercase,
    bool RequireSymbol);

/// <summary>Password registration, sign-in and recovery through the gateway cookie session.</summary>
public sealed class HttpPasswordAuthClient(CookieSessionHttpClient session)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CookieSessionHttpClient _session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<PasswordPolicyInfo?> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _session.GetAsync("/auth/password/policy", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await ReadAsync<PasswordPolicyInfo>(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<PasswordAuthOutcome> RegisterAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PostJsonAsync(
                "/auth/password/register", new { email, password }, token, cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => Succeeded(await ReadEmailAsync(response, cancellationToken).ConfigureAwait(false)),
                HttpStatusCode.Conflict => new PasswordAuthOutcome(
                    PasswordAuthStatus.DuplicateEmail,
                    Message: "An account with this email already exists. Sign in instead."),
                HttpStatusCode.BadRequest => new PasswordAuthOutcome(
                    PasswordAuthStatus.InvalidRequest,
                    Message: await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                        ?? "Check the email and password requirements."),
                _ => Unavailable("Registration is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Registration is temporarily unavailable.");
        }
    }

    public async Task<PasswordAuthOutcome> LoginAsync(
        string email,
        string password,
        bool rememberMe,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PostJsonAsync(
                "/auth/password/login", new { email, password, rememberMe }, token, cancellationToken)
                .ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => Succeeded(await ReadEmailAsync(response, cancellationToken).ConfigureAwait(false)),
                HttpStatusCode.Unauthorized => new PasswordAuthOutcome(
                    PasswordAuthStatus.InvalidCredentials,
                    Message: "The email or password is incorrect."),
                HttpStatusCode.Conflict => new PasswordAuthOutcome(
                    PasswordAuthStatus.InvalidRequest,
                    Message: "You are already signed in."),
                HttpStatusCode.BadRequest => new PasswordAuthOutcome(
                    PasswordAuthStatus.InvalidRequest,
                    Message: await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                        ?? "The sign-in request is invalid."),
                _ => Unavailable("Sign-in is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Sign-in is temporarily unavailable.");
        }
    }

    public async Task<PasswordAuthOutcome> LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PostJsonAsync(
                "/auth/logout", new { }, token, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable("Sign-out is temporarily unavailable.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Sign-out is temporarily unavailable.");
        }
        finally
        {
            _session.ClearCookies();
        }

        return new PasswordAuthOutcome(PasswordAuthStatus.Succeeded);
    }

    public async Task<PasswordAuthOutcome> RequestPasswordResetAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PostJsonAsync(
                "/auth/password/reset/request", new { email }, token, cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new PasswordAuthOutcome(
                    PasswordAuthStatus.Succeeded,
                    Message: await ReadNeutralMessageAsync(response, cancellationToken).ConfigureAwait(false)),
                HttpStatusCode.ServiceUnavailable => Unavailable("Password reset is temporarily unavailable."),
                _ => new PasswordAuthOutcome(
                    PasswordAuthStatus.InvalidRequest,
                    Message: "Enter a valid email address.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Password reset is temporarily unavailable.");
        }
    }

    public async Task<PasswordAuthOutcome> RequestEmailCodeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PostJsonAsync(
                "/auth/email/otp/request", new { }, token, cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new PasswordAuthOutcome(
                    PasswordAuthStatus.Succeeded,
                    Message: "A verification code has been sent to your email address."),
                HttpStatusCode.TooManyRequests => new PasswordAuthOutcome(
                    PasswordAuthStatus.Throttled,
                    Message: "A code was sent recently. Wait before requesting a new one."),
                HttpStatusCode.Unauthorized => new PasswordAuthOutcome(
                    PasswordAuthStatus.SignedOut,
                    Message: "Sign in to verify your email address."),
                _ => Unavailable("Verification codes are temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Verification codes are temporarily unavailable.");
        }
    }

    public async Task<PasswordAuthOutcome> ConfirmEmailCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _session.GetAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _session.PostJsonAsync(
                "/auth/email/otp/confirm", new { code }, token, cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new PasswordAuthOutcome(PasswordAuthStatus.Succeeded),
                HttpStatusCode.Unauthorized => new PasswordAuthOutcome(
                    PasswordAuthStatus.SignedOut,
                    Message: "Sign in to verify your email address."),
                _ => new PasswordAuthOutcome(
                    PasswordAuthStatus.InvalidRequest,
                    Message: await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
                        ?? "The code is invalid or has expired.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Verification is temporarily unavailable.");
        }
    }

    private static PasswordAuthOutcome Succeeded(string? email) =>
        new(PasswordAuthStatus.Succeeded, Email: email);

    private static PasswordAuthOutcome Unavailable(string message) =>
        new(PasswordAuthStatus.Unavailable, Message: message);

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

    private static async Task<string?> ReadEmailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.TryGetProperty("email", out var email)
                ? email.GetString()
                : null;
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
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }

            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadNeutralMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        await ReadMessageAsync(response, cancellationToken).ConfigureAwait(false)
        ?? "If an account matches this address, reset instructions have been sent.";
}
