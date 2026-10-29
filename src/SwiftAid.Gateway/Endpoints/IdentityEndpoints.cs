using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Maps the fixed external-login and SwiftAid authorization-code endpoints.</summary>
public static class IdentityEndpoints
{
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string ReauthenticatedAtKey = "swiftaid:reauthenticated-at-utc";
    private const string ReauthenticatedProviderKey = "swiftaid:reauthenticated-provider";
    private static readonly TimeSpan RecentAuthenticationWindow = TimeSpan.FromMinutes(5);

    public static void MapSwiftAidIdentityEndpoints(
        this IEndpointRouteBuilder endpoints,
        FederationOptions federationOptions,
        IdentityServerOptions identityServerOptions)
    {
        ArgumentNullException.ThrowIfNull(federationOptions);
        ArgumentNullException.ThrowIfNull(identityServerOptions);
        var group = endpoints.MapGroup("/auth");
        group.MapGet("/providers", GetProviders);
        group.MapGet("/facebook/web/options", GetFacebookWebLoginOptions);
        group.MapPost("/facebook/web/exchange", ExchangeFacebookWebLogin);
        if (federationOptions.Enabled)
        {
            group.MapGet("/antiforgery", GetAntiforgeryToken);
            group.MapPost("/{providerId}/challenge", ChallengeProvider);
            group.MapGet("/external/callback", CompleteExternalLogin);
            group.MapPost("/reauthenticate/{providerId}", ReauthenticateProvider);
            group.MapGet("/reauthenticate/callback", CompleteReauthentication);
            group.MapGet("/identities", GetLinkedIdentities);
            group.MapPost("/identities/{providerId}/link", LinkExternalIdentity);
            group.MapGet("/identities/link/callback", CompleteExternalIdentityLink);
            group.MapPost("/identities/{providerId}/unlink", UnlinkExternalIdentity);
            group.MapPost("/logout", SignOutSwiftAid);
        }

        group.MapGet("/password/policy", GetPasswordPolicy);
        group.MapPost("/password/register", RegisterPasswordUser);
        group.MapPost("/password/login", LoginPasswordUser);
        group.MapPost("/password/change", ChangePasswordUser);
        group.MapPost("/password/reset/request", RequestPasswordReset);
        group.MapPost("/password/reset/confirm", ConfirmPasswordReset);
        group.MapGet("/email/status", GetEmailVerificationStatus);
        group.MapPost("/email/otp/request", RequestEmailOtp);
        group.MapPost("/email/otp/confirm", ConfirmEmailOtp);
        group.MapGet("/profile", GetUserProfile);
        group.MapPut("/profile", SaveUserProfile);
        group.MapGet("/notifications/preferences", GetNotificationPreferences);
        group.MapPut("/notifications/preferences", SaveNotificationPreferences);
        group.MapGet("/organizations/mine", GetMyMemberships);
        group.MapGet("/organizations/directory", SearchOrganizations);
        group.MapGet("/organizations/requests/mine", GetMyMembershipRequests);
        group.MapPost("/organizations/requests", SubmitMembershipRequest);

        if (identityServerOptions.Enabled)
        {
            endpoints.MapMethods("/connect/authorize", ["GET", "POST"], AuthorizeClient);
            endpoints.MapPost("/connect/token", ExchangeToken);
            endpoints.MapGet("/connect/logout", EndSession);
        }
    }

    private static IResult GetProviders(FederationOptions options)
    {
        var providers = new List<ProviderAvailability>();
        if (options.Google.Enabled)
        {
            providers.Add(new ProviderAvailability("google", "Google", options.Google.CallbackVerified));
        }

        if (options.Facebook.Enabled)
        {
            providers.Add(new ProviderAvailability("facebook", "Facebook", options.Facebook.CallbackVerified));
        }

        if (options.Enabled)
        {
            providers.AddRange(options.OrganizationProviders.Select(provider =>
            new ProviderAvailability(
                provider.ProviderId,
                string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderId : provider.DisplayName,
                provider.CallbackVerified)));
        }
        return Results.Ok(providers);
    }

    private static IResult GetFacebookWebLoginOptions(FederationOptions options, HttpContext context)
    {
        SetNoStore(context);
        var available = IsFacebookWebLoginAvailable(options);
        return Results.Ok(available
            ? new FacebookWebLoginOptionsResponse(
                true,
                options.Facebook.ClientId,
                options.FacebookWebLogin.GraphApiVersion)
            : new FacebookWebLoginOptionsResponse(false));
    }

    private static async Task<IResult> ExchangeFacebookWebLogin(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var options = context.RequestServices.GetRequiredService<FederationOptions>();
        if (!IsFacebookWebLoginAvailable(options))
        {
            return FacebookWebError(context, StatusCodes.Status503ServiceUnavailable, "web_login_disabled");
        }

        if (!IsJsonContentType(context.Request.ContentType))
        {
            return FacebookWebError(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media");
        }

        var maximumRequestBytes = Math.Clamp(options.FacebookWebLogin.MaximumRequestBytes, 256, 16384);
        if (context.Request.ContentLength is > 0 and var contentLength && contentLength > maximumRequestBytes)
        {
            return FacebookWebError(context, StatusCodes.Status413PayloadTooLarge, "oversized");
        }

        try
        {
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return FacebookWebError(context, StatusCodes.Status403Forbidden, "antiforgery_invalid");
        }
        catch (InvalidOperationException)
        {
            return FacebookWebError(context, StatusCodes.Status400BadRequest, "unsupported_media");
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return FacebookWebError(context, StatusCodes.Status409Conflict, "already_signed_in");
        }

        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        FacebookWebRateLimitStatus rateStatus;
        try
        {
            var rateLimiter = context.RequestServices.GetRequiredService<IFacebookWebLoginRateLimiter>();
            rateStatus = await rateLimiter.CheckAsync(
                address,
                options.FacebookWebLogin.MaximumAttempts,
                TimeSpan.FromSeconds(options.FacebookWebLogin.RateLimitWindowSeconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FacebookWebError(context, StatusCodes.Status503ServiceUnavailable, "provider_unavailable");
        }
        if (rateStatus == FacebookWebRateLimitStatus.Limited)
        {
            return FacebookWebError(context, StatusCodes.Status429TooManyRequests, "rate_limited");
        }

        if (rateStatus != FacebookWebRateLimitStatus.Allowed)
        {
            return FacebookWebError(context, StatusCodes.Status503ServiceUnavailable, "provider_unavailable");
        }

        var bodyRead = await ReadFacebookExchangeRequestAsync(
            context.Request,
            maximumRequestBytes,
            cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status == FacebookExchangeBodyStatus.Oversized)
        {
            return FacebookWebError(context, StatusCodes.Status413PayloadTooLarge, "oversized");
        }

        if (bodyRead.Status != FacebookExchangeBodyStatus.Valid ||
            bodyRead.Request is null ||
            string.IsNullOrWhiteSpace(bodyRead.Request.AccessToken) ||
            bodyRead.Request.AccessToken.Length > 4096)
        {
            return FacebookWebError(context, StatusCodes.Status400BadRequest, "invalid_request");
        }

        var validator = context.RequestServices.GetRequiredService<IFacebookWebTokenValidator>();
        var accessToken = bodyRead.Request.AccessToken;
        bodyRead.Request.AccessToken = string.Empty;
        FacebookTokenValidationResult tokenResult;
        try
        {
            tokenResult = await validator.ValidateAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            accessToken = string.Empty;
        }
        if (tokenResult.Status == FacebookTokenValidationStatus.Invalid)
        {
            return FacebookWebError(context, StatusCodes.Status401Unauthorized, "invalid_provider_token");
        }

        if (tokenResult.Status != FacebookTokenValidationStatus.Valid ||
            string.IsNullOrWhiteSpace(tokenResult.Subject))
        {
            return FacebookWebError(context, StatusCodes.Status503ServiceUnavailable, "provider_unavailable");
        }

        try
        {
            var resolver = context.RequestServices.GetRequiredService<IExternalIdentityResolver>();
            var resolution = await resolver.ResolveAsync(
                new ValidatedExternalIdentity(
                    FederationSetup.FacebookProviderId,
                    FederationSetup.FacebookIssuer,
                    tokenResult.Subject),
                cancellationToken).ConfigureAwait(false);
            if (resolution.Status == ExternalIdentityResolutionStatus.Unavailable)
            {
                return FacebookWebError(context, StatusCodes.Status503ServiceUnavailable, "provider_unavailable");
            }

            if (resolution.Status != ExternalIdentityResolutionStatus.Resolved ||
                string.IsNullOrWhiteSpace(resolution.ActorId))
            {
                return FacebookWebError(context, StatusCodes.Status401Unauthorized, "invalid_provider_token");
            }

            var userManager = context.RequestServices.GetRequiredService<UserManager<SwiftAidUser>>();
            var user = await userManager.FindByIdAsync(resolution.ActorId).ConfigureAwait(false);
            if (user is null)
            {
                return FacebookWebError(context, StatusCodes.Status401Unauthorized, "invalid_provider_token");
            }

            var signInManager = context.RequestServices.GetRequiredService<SignInManager<SwiftAidUser>>();
            await signInManager.SignInAsync(
                user,
                new AuthenticationProperties { IsPersistent = false },
                authenticationMethod: FederationSetup.FacebookProviderId).ConfigureAwait(false);

            return Results.Json(
                new FacebookWebLoginExchangeResponse(
                    IsLocalReturnUrl(bodyRead.Request.ReturnUrl) ? bodyRead.Request.ReturnUrl! : "/"),
                statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FacebookWebError(context, StatusCodes.Status503ServiceUnavailable, "provider_unavailable");
        }
    }

    private static bool IsFacebookWebLoginAvailable(FederationOptions options) =>
        options.Enabled && options.Facebook.Enabled && options.Facebook.CallbackVerified &&
        options.FacebookWebLogin.Enabled && !string.IsNullOrWhiteSpace(options.Facebook.ClientId);

    private static bool IsJsonContentType(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed) &&
        string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task<FacebookExchangeBodyReadResult> ReadFacebookExchangeRequestAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maximumBytes + 1];
        var total = 0;
        try
        {
            while (true)
            {
                var remaining = maximumBytes + 1 - total;
                if (remaining <= 0)
                {
                    return new FacebookExchangeBodyReadResult(FacebookExchangeBodyStatus.Oversized);
                }

                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(total, Math.Min(1024, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > maximumBytes)
            {
                return new FacebookExchangeBodyReadResult(FacebookExchangeBodyStatus.Oversized);
            }

            var payload = JsonSerializer.Deserialize<FacebookWebLoginExchangeRequest>(
                buffer.AsSpan(0, total),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return payload is null
                ? new FacebookExchangeBodyReadResult(FacebookExchangeBodyStatus.Invalid)
                : new FacebookExchangeBodyReadResult(FacebookExchangeBodyStatus.Valid, payload);
        }
        catch (JsonException)
        {
            return new FacebookExchangeBodyReadResult(FacebookExchangeBodyStatus.Invalid);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static IResult FacebookWebError(HttpContext context, int statusCode, string code)
    {
        SetNoStore(context);
        return Results.Json(new FacebookWebLoginError(code), statusCode: statusCode);
    }

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache";
        context.Response.Headers.Pragma = "no-cache";
    }

    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Ok(new AntiforgeryTokenResponse(tokens.RequestToken ?? string.Empty, CsrfHeaderName));
    }

    private static async Task<IResult> ChallengeProvider(
        string providerId,
        string? returnUrl,
        HttpContext context,
        IAntiforgery antiforgery,
        FederationOptions options)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "The sign-in request is invalid." });
        }
        catch (InvalidOperationException)
        {
            return Results.BadRequest(new { error = "The sign-in request is invalid." });
        }

        if (!IsLocalReturnUrl(returnUrl) || !TryGetVerifiedProvider(options, providerId, out var scheme, out _))
        {
            return Results.NotFound();
        }

        var callback = QueryHelpers.AddQueryString(
            "/auth/external/callback",
            new Dictionary<string, string?>
            {
                ["provider"] = providerId,
                ["returnUrl"] = returnUrl
            });
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = callback },
            [scheme]);
    }

    private static async Task<IResult> CompleteExternalLogin(
        string provider,
        string? returnUrl,
        HttpContext context,
        FederationOptions federationOptions,
        IExternalIdentityResolver resolver,
        SignInManager<SwiftAidUser> signInManager,
        UserManager<SwiftAidUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!IsLocalReturnUrl(returnUrl) ||
            !TryGetVerifiedProvider(federationOptions, provider, out var expectedScheme, out var issuer))
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
            return Results.NotFound();
        }

        var external = await context.AuthenticateAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
        var externalLogin = await signInManager.GetExternalLoginInfoAsync().ConfigureAwait(false);
        if (!external.Succeeded || external.Principal?.Identity?.IsAuthenticated != true ||
            externalLogin is null ||
            !string.Equals(externalLogin.LoginProvider, expectedScheme, StringComparison.Ordinal))
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
            return Results.Unauthorized();
        }

        try
        {
            var resolution = await resolver.ResolveAsync(
                new ValidatedExternalIdentity(provider, issuer, externalLogin.ProviderKey),
                cancellationToken).ConfigureAwait(false);
            if (resolution.Status == ExternalIdentityResolutionStatus.Unavailable)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Sign-in is temporarily unavailable.");
            }

            if (resolution.Status != ExternalIdentityResolutionStatus.Resolved ||
                string.IsNullOrWhiteSpace(resolution.ActorId))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(resolution.ActorId).ConfigureAwait(false);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var authenticatedAt = timeProvider.GetUtcNow();
            var properties = CreateRecentAuthenticationProperties(authenticatedAt, provider);
            await signInManager.SignInAsync(user, properties, authenticationMethod: provider)
                .ConfigureAwait(false);
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return Results.LocalRedirect(returnUrl!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not AntiforgeryValidationException)
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Sign-in is temporarily unavailable.");
        }
        finally
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> ReauthenticateProvider(
        string providerId,
        string? returnUrl,
        HttpContext context,
        IAntiforgery antiforgery,
        FederationOptions options,
        SwiftAidDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "The reauthentication request is invalid." });
        }
        catch (InvalidOperationException)
        {
            return Results.BadRequest(new { error = "The reauthentication request is invalid." });
        }

        if (!TryGetCurrentActor(context, out var actor) ||
            !await IsCurrentActorAsync(actor, dbContext, timeProvider, cancellationToken).ConfigureAwait(false) ||
            !IsLocalReturnUrl(returnUrl) ||
            !TryGetVerifiedProvider(options, providerId, out var scheme, out _))
        {
            return Results.NotFound();
        }

        var callback = QueryHelpers.AddQueryString(
            "/auth/reauthenticate/callback",
            new Dictionary<string, string?>
            {
                ["provider"] = providerId,
                ["returnUrl"] = returnUrl
            });
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = callback },
            [scheme]);
    }

    private static async Task<IResult> CompleteReauthentication(
        string provider,
        string? returnUrl,
        HttpContext context,
        FederationOptions federationOptions,
        IExternalIdentityMappingStore mappingStore,
        SwiftAidDbContext dbContext,
        SignInManager<SwiftAidUser> signInManager,
        UserManager<SwiftAidUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentActor(context, out var currentActor) ||
            !await IsCurrentActorAsync(currentActor, dbContext, timeProvider, cancellationToken).ConfigureAwait(false) ||
            !IsLocalReturnUrl(returnUrl) ||
            !TryGetVerifiedProvider(federationOptions, provider, out var expectedScheme, out var issuer))
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
            return Results.Unauthorized();
        }

        var external = await context.AuthenticateAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
        var externalLogin = await signInManager.GetExternalLoginInfoAsync().ConfigureAwait(false);
        if (!external.Succeeded || external.Principal?.Identity?.IsAuthenticated != true ||
            externalLogin is null ||
            !string.Equals(externalLogin.LoginProvider, expectedScheme, StringComparison.Ordinal))
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
            return Results.Unauthorized();
        }

        try
        {
            var resolved = await mappingStore.ResolveExistingAsync(
                provider,
                issuer,
                externalLogin.ProviderKey,
                cancellationToken).ConfigureAwait(false);
            if (resolved.Status != ExternalIdentityResolutionStatus.Resolved ||
                !string.Equals(resolved.ActorId, currentActor.ActorId, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(currentActor.ActorId).ConfigureAwait(false);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var properties = CreateRecentAuthenticationProperties(timeProvider.GetUtcNow(), provider);
            await signInManager.SignInAsync(user, properties, authenticationMethod: provider)
                .ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return Results.LocalRedirect(returnUrl!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Reauthentication is temporarily unavailable.");
        }
        finally
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> GetLinkedIdentities(
        HttpContext context,
        SwiftAidDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!TryGetCurrentActor(context, out var actor) ||
            !await IsCurrentActorAsync(actor, dbContext, timeProvider, cancellationToken).ConfigureAwait(false))
        {
            return Results.Unauthorized();
        }

        var providers = await dbContext.ExternalIdentityMappings.AsNoTracking()
            .Where(mapping => mapping.ActorId == actor.ActorId)
            .Select(mapping => mapping.Provider)
            .Distinct()
            .OrderBy(provider => provider)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(providers);
    }

    private static async Task<IResult> LinkExternalIdentity(
        string providerId,
        string? returnUrl,
        HttpContext context,
        IAntiforgery antiforgery,
        FederationOptions options,
        SwiftAidDbContext dbContext,
        TimeProvider timeProvider)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "The account-link request is invalid." });
        }
        catch (InvalidOperationException)
        {
            return Results.BadRequest(new { error = "The account-link request is invalid." });
        }

        if (!TryGetCurrentActor(context, out var actor) ||
            !await IsCurrentActorAsync(actor, dbContext, timeProvider, context.RequestAborted).ConfigureAwait(false) ||
            !await HasRecentAuthenticationAsync(context, timeProvider).ConfigureAwait(false))
        {
            return Results.Conflict(new { error = "Reauthenticate with a linked provider before linking an account." });
        }

        if (!IsLocalReturnUrl(returnUrl) ||
            !TryGetVerifiedProvider(options, providerId, out var scheme, out _))
        {
            return Results.NotFound();
        }

        var callback = QueryHelpers.AddQueryString(
            "/auth/identities/link/callback",
            new Dictionary<string, string?>
            {
                ["provider"] = providerId,
                ["returnUrl"] = returnUrl
            });
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = callback },
            [scheme]);
    }

    private static async Task<IResult> CompleteExternalIdentityLink(
        string provider,
        string? returnUrl,
        HttpContext context,
        FederationOptions federationOptions,
        IExternalIdentityMappingStore mappingStore,
        SwiftAidDbContext dbContext,
        SignInManager<SwiftAidUser> signInManager,
        UserManager<SwiftAidUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentActor(context, out var actor) ||
            !await IsCurrentActorAsync(actor, dbContext, timeProvider, cancellationToken).ConfigureAwait(false) ||
            !await HasRecentAuthenticationAsync(context, timeProvider).ConfigureAwait(false) ||
            !IsLocalReturnUrl(returnUrl) ||
            !TryGetVerifiedProvider(federationOptions, provider, out var expectedScheme, out var issuer))
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
            return Results.Unauthorized();
        }

        var external = await context.AuthenticateAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
        var externalLogin = await signInManager.GetExternalLoginInfoAsync().ConfigureAwait(false);
        if (!external.Succeeded || external.Principal?.Identity?.IsAuthenticated != true ||
            externalLogin is null ||
            !string.Equals(externalLogin.LoginProvider, expectedScheme, StringComparison.Ordinal))
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
            return Results.Unauthorized();
        }

        try
        {
            var result = await mappingStore.LinkAsync(
                actor.ActorId,
                provider,
                issuer,
                externalLogin.ProviderKey,
                cancellationToken).ConfigureAwait(false);
            if (result == ExternalIdentityLinkStatus.Conflict)
            {
                return Results.Conflict(new { error = "This external account is already linked to another SwiftAid account." });
            }

            if (result == ExternalIdentityLinkStatus.ActorUnavailable ||
                result == ExternalIdentityLinkStatus.Rejected)
            {
                return Results.Unauthorized();
            }

            if (result == ExternalIdentityLinkStatus.Linked)
            {
                var user = await userManager.FindByIdAsync(actor.ActorId).ConfigureAwait(false);
                if (user is null)
                {
                    return Results.Unauthorized();
                }

                await signInManager.SignInAsync(user, isPersistent: false).ConfigureAwait(false);
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.LocalRedirect(returnUrl!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Account linking is temporarily unavailable.");
        }
        finally
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> UnlinkExternalIdentity(
        string providerId,
        HttpContext context,
        IAntiforgery antiforgery,
        IExternalIdentityMappingStore mappingStore,
        SwiftAidDbContext dbContext,
        SignInManager<SwiftAidUser> signInManager,
        UserManager<SwiftAidUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "The account-unlink request is invalid." });
        }
        catch (InvalidOperationException)
        {
            return Results.BadRequest(new { error = "The account-unlink request is invalid." });
        }

        if (!TryGetCurrentActor(context, out var actor) ||
            !await IsCurrentActorAsync(actor, dbContext, timeProvider, cancellationToken).ConfigureAwait(false))
        {
            return Results.Unauthorized();
        }

        if (!await HasRecentAuthenticationAsync(context, timeProvider).ConfigureAwait(false))
        {
            return Results.Conflict(new { error = "Reauthenticate with a linked provider before unlinking an account." });
        }

        var result = await mappingStore.UnlinkProviderAsync(
            actor.ActorId,
            providerId,
            cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case ExternalIdentityUnlinkStatus.LastLoginRoute:
                return Results.Conflict(new { error = "At least one verified sign-in method must remain linked." });
            case ExternalIdentityUnlinkStatus.NotLinked:
                return Results.NotFound();
            case ExternalIdentityUnlinkStatus.ActorUnavailable:
            case ExternalIdentityUnlinkStatus.Rejected:
                return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(actor.ActorId).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await signInManager.SignInAsync(user, isPersistent: false).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return Results.NoContent();
    }

    private static async Task<IResult> SignOutSwiftAid(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "The sign-out request is invalid." });
        }
        catch (InvalidOperationException)
        {
            return Results.BadRequest(new { error = "The sign-out request is invalid." });
        }

        await context.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return Results.NoContent();
    }

    private static IResult GetPasswordPolicy(IOptions<IdentityOptions> identityOptions, HttpContext context)
    {
        SetNoStore(context);
        var password = identityOptions.Value.Password;
        return Results.Ok(new PasswordPolicyResponse(
            password.RequiredLength,
            password.RequireDigit,
            password.RequireLowercase,
            password.RequireUppercase,
            password.RequireNonAlphanumeric,
            password.RequiredUniqueChars));
    }

    private static async Task<IResult> RegisterPasswordUser(
        HttpContext context,
        IAntiforgery antiforgery,
        IPasswordIdentityRegistrar registrar,
        UserManager<SwiftAidUser> userManager,
        SignInManager<SwiftAidUser> signInManager,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The registration request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The registration request is invalid.");
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return PasswordError(context, StatusCodes.Status409Conflict, "You are already signed in.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter an email address and password.");
        }

        var email = bodyRead.Request.Email?.Trim() ?? string.Empty;
        var password = bodyRead.Request.Password ?? string.Empty;
        bodyRead.Request.Password = string.Empty;
        if (!IsValidPasswordEmail(email) || string.IsNullOrEmpty(password) || password.Length > 256)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter a valid email address and password.");
        }

        try
        {
            var registration = await registrar.RegisterAsync(email, password, cancellationToken)
                .ConfigureAwait(false);
            if (registration.Status == PasswordRegistrationStatus.DuplicateEmail)
            {
                return PasswordError(
                    context,
                    StatusCodes.Status409Conflict,
                    "An account with this email already exists. Sign in instead.");
            }

            if (registration.Status == PasswordRegistrationStatus.Unavailable)
            {
                return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Registration is temporarily unavailable.");
            }

            if (registration.Status != PasswordRegistrationStatus.Registered ||
                string.IsNullOrWhiteSpace(registration.ActorId))
            {
                return PasswordError(
                    context,
                    StatusCodes.Status400BadRequest,
                    registration.Errors?.FirstOrDefault() ?? "The password does not meet the requirements.");
            }

            var user = await userManager.FindByIdAsync(registration.ActorId).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Registration is temporarily unavailable.");
            }

            await signInManager.SignInAsync(user, isPersistent: false).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new PasswordAuthResponse(user.Email ?? email));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Registration is temporarily unavailable.");
        }
    }

    private static async Task<IResult> LoginPasswordUser(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        SignInManager<SwiftAidUser> signInManager,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The sign-in request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The sign-in request is invalid.");
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return PasswordError(context, StatusCodes.Status409Conflict, "You are already signed in.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter an email address and password.");
        }

        var email = bodyRead.Request.Email?.Trim() ?? string.Empty;
        var password = bodyRead.Request.Password ?? string.Empty;
        var rememberMe = bodyRead.Request.RememberMe;
        bodyRead.Request.Password = string.Empty;
        if (!IsValidPasswordEmail(email) || string.IsNullOrEmpty(password) || password.Length > 256)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "The email or password is incorrect.");
        }

        try
        {
            var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "The email or password is incorrect.");
            }

            var check = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)
                .ConfigureAwait(false);
            if (!check.Succeeded)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "The email or password is incorrect.");
            }

            await signInManager.SignInAsync(user, isPersistent: rememberMe).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new PasswordAuthResponse(user.Email ?? email));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Sign-in is temporarily unavailable.");
        }
    }

    private static async Task<IResult> ChangePasswordUser(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        SignInManager<SwiftAidUser> signInManager,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The change-password request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The change-password request is invalid.");
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to change your password.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter the current and a new password.");
        }

        var currentPassword = bodyRead.Request.CurrentPassword ?? string.Empty;
        var newPassword = bodyRead.Request.NewPassword ?? string.Empty;
        bodyRead.Request.CurrentPassword = string.Empty;
        bodyRead.Request.NewPassword = string.Empty;
        if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword) ||
            currentPassword.Length > 256 || newPassword.Length > 256)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter the current and a new password.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to change your password.");
            }

            var changed = await userManager.ChangePasswordAsync(user, currentPassword, newPassword)
                .ConfigureAwait(false);
            if (!changed.Succeeded)
            {
                var failure = changed.Errors.FirstOrDefault();
                return PasswordError(
                    context,
                    StatusCodes.Status400BadRequest,
                    string.IsNullOrWhiteSpace(failure?.Description)
                        ? "The password could not be changed."
                        : failure.Description);
            }

            await signInManager.RefreshSignInAsync(user).ConfigureAwait(false);
            SetNoStore(context);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Changing the password is temporarily unavailable.");
        }
    }

    private static async Task<IResult> RequestPasswordReset(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        IEmailSender<SwiftAidUser> emailSender,
        IOptions<SmtpEmailOptions> smtpOptions,
        IOptions<PublicSiteOptions> siteOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The reset request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The reset request is invalid.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter a valid email address.");
        }

        var email = bodyRead.Request.Email?.Trim() ?? string.Empty;
        if (!IsValidPasswordEmail(email))
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter a valid email address.");
        }

        try
        {
            var smtp = smtpOptions.Value;
            if (!smtp.Enabled ||
                string.IsNullOrWhiteSpace(smtp.Host) ||
                string.IsNullOrWhiteSpace(smtp.FromAddress))
            {
                return PasswordError(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "Password reset is temporarily unavailable. Try again later.");
            }

            var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
            if (user is not null && !string.IsNullOrWhiteSpace(user.Email))
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
                var code = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));
                var resetLink = BuildPasswordResetLink(context, siteOptions.Value, user.Id, code);
                await emailSender.SendPasswordResetLinkAsync(user, user.Email, resetLink).ConfigureAwait(false);
            }

            SetNoStore(context);
            return Results.Ok(new PasswordResetRequestResponse(
                "If an account matches this address, reset instructions have been sent."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Password reset is temporarily unavailable. Try again later.");
        }
    }

    private static async Task<IResult> ConfirmPasswordReset(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The reset request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The reset request is invalid.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The reset link is invalid or has expired.");
        }

        var userId = bodyRead.Request.UserId?.Trim() ?? string.Empty;
        var code = bodyRead.Request.Code?.Trim() ?? string.Empty;
        var newPassword = bodyRead.Request.NewPassword ?? string.Empty;
        bodyRead.Request.Code = string.Empty;
        bodyRead.Request.NewPassword = string.Empty;
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 128 ||
            string.IsNullOrWhiteSpace(code) || code.Length > 4096 ||
            string.IsNullOrEmpty(newPassword) || newPassword.Length > 256)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The reset link is invalid or has expired.");
        }

        try
        {
            var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status400BadRequest, "The reset link is invalid or has expired.");
            }

            string token;
            try
            {
                token = System.Text.Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            }
            catch (FormatException)
            {
                return PasswordError(context, StatusCodes.Status400BadRequest, "The reset link is invalid or has expired.");
            }

            var reset = await userManager.ResetPasswordAsync(user, token, newPassword).ConfigureAwait(false);
            if (!reset.Succeeded)
            {
                var failure = reset.Errors.FirstOrDefault();
                if (failure is not null &&
                    !string.Equals(failure.Code, "InvalidToken", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(failure.Description))
                {
                    return PasswordError(context, StatusCodes.Status400BadRequest, failure.Description);
                }

                return PasswordError(context, StatusCodes.Status400BadRequest, "The reset link is invalid or has expired.");
            }

            SetNoStore(context);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Password reset is temporarily unavailable. Try again later.");
        }
    }

    private static bool IsValidPasswordEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 256)
        {
            return false;
        }

        try
        {
            _ = new MailAddress(email);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string BuildPasswordResetLink(
        HttpContext context,
        PublicSiteOptions siteOptions,
        string userId,
        string code)
    {
        var configured = siteOptions.BaseUrl?.Trim().TrimEnd('/');
        var origin = string.IsNullOrWhiteSpace(configured)
            ? $"{context.Request.Scheme}://{context.Request.Host}"
            : configured;
        return QueryHelpers.AddQueryString(
            $"{origin}/reset-password",
            new Dictionary<string, string?>
            {
                ["userId"] = userId,
                ["code"] = code
            });
    }

    private static async Task<PasswordBodyReadResult> ReadPasswordRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 4096;
        if (!IsJsonContentType(request.ContentType))
        {
            return new PasswordBodyReadResult(PasswordBodyStatus.Invalid);
        }

        if (request.ContentLength > maximumBytes)
        {
            return new PasswordBodyReadResult(PasswordBodyStatus.Oversized);
        }

        var buffer = new byte[maximumBytes + 1];
        var total = 0;
        try
        {
            while (true)
            {
                var remaining = maximumBytes + 1 - total;
                if (remaining <= 0)
                {
                    return new PasswordBodyReadResult(PasswordBodyStatus.Oversized);
                }

                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(total, Math.Min(1024, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > maximumBytes)
            {
                return new PasswordBodyReadResult(PasswordBodyStatus.Oversized);
            }

            var payload = JsonSerializer.Deserialize<PasswordRequest>(
                buffer.AsSpan(0, total),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return payload is null
                ? new PasswordBodyReadResult(PasswordBodyStatus.Invalid)
                : new PasswordBodyReadResult(PasswordBodyStatus.Valid, payload);
        }
        catch (JsonException)
        {
            return new PasswordBodyReadResult(PasswordBodyStatus.Invalid);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task<IResult> GetEmailVerificationStatus(
        HttpContext context,
        UserManager<SwiftAidUser> userManager,
        IEmailVerificationService verificationService,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to view verification status.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to view verification status.");
            }

            var verified = await verificationService.IsVerifiedAsync(user, cancellationToken).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new EmailVerificationStatusResponse(verified, user.Email));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Verification status is temporarily unavailable.");
        }
    }

    private static async Task<IResult> RequestEmailOtp(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        IEmailVerificationService verificationService,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The verification request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The verification request is invalid.");
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to verify your email address.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to verify your email address.");
            }

            var outcome = await verificationService.RequestCodeAsync(user, cancellationToken).ConfigureAwait(false);
            return outcome.Status switch
            {
                EmailVerificationStatus.Sent => Results.Ok(new EmailOtpRequestResponse(
                    "A verification code has been sent to your email address.",
                    outcome.RetryAfterSeconds)),
                EmailVerificationStatus.Throttled => ThrottledEmailOtp(context, outcome),
                _ => PasswordError(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "Verification codes are temporarily unavailable. Try again later.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Verification codes are temporarily unavailable. Try again later.");
        }
        finally
        {
            SetNoStore(context);
        }
    }

    private static async Task<IResult> ConfirmEmailOtp(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        IEmailVerificationService verificationService,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The verification request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The verification request is invalid.");
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to verify your email address.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter the verification code.");
        }

        var code = bodyRead.Request.Code?.Trim() ?? string.Empty;
        bodyRead.Request.Code = string.Empty;
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter the verification code.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to verify your email address.");
            }

            var outcome = await verificationService.ConfirmCodeAsync(user, code, cancellationToken).ConfigureAwait(false);
            return outcome.Status switch
            {
                EmailVerificationStatus.Verified => Results.Ok(new EmailOtpConfirmResponse(true)),
                EmailVerificationStatus.InvalidCode => PasswordError(
                    context,
                    StatusCodes.Status400BadRequest,
                    "The code is incorrect. Check the latest email and try again."),
                EmailVerificationStatus.TooManyAttempts => PasswordError(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Too many incorrect attempts. Request a new code."),
                EmailVerificationStatus.Unavailable => PasswordError(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "Verification is temporarily unavailable. Try again later."),
                _ => PasswordError(
                    context,
                    StatusCodes.Status400BadRequest,
                    "The code is invalid or has expired. Request a new one.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Verification is temporarily unavailable. Try again later.");
        }
        finally
        {
            SetNoStore(context);
        }
    }

    private static async Task<IResult> GetUserProfile(
        HttpContext context,
        UserManager<SwiftAidUser> userManager,
        IUserProfileService profileService,
        IEmailVerificationService verificationService,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to view your profile.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to view your profile.");
            }

            var profile = await profileService.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);
            var verified = await verificationService.IsVerifiedAsync(user, cancellationToken).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new UserProfileResponse(
                profile?.DisplayName,
                profile?.Phone,
                profile?.Country,
                profile?.Region,
                profile?.Locality,
                user.Email,
                verified));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Your profile is temporarily unavailable.");
        }
    }

    private static async Task<IResult> SaveUserProfile(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        IUserProfileService profileService,
        IEmailVerificationService verificationService,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The profile request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The profile request is invalid.");
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to save your profile.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Enter your profile details.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to save your profile.");
            }

            var saved = await profileService.SaveAsync(
                user.Id,
                new UserProfileData(
                    bodyRead.Request.DisplayName,
                    bodyRead.Request.Phone,
                    bodyRead.Request.Country,
                    bodyRead.Request.Region,
                    bodyRead.Request.Locality),
                cancellationToken).ConfigureAwait(false);
            if (saved.Status == UserProfileSaveStatus.Invalid)
            {
                return PasswordError(
                    context,
                    StatusCodes.Status400BadRequest,
                    saved.Errors?.FirstOrDefault() ?? "Check your profile details and try again.");
            }

            if (saved.Status != UserProfileSaveStatus.Saved)
            {
                return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Saving your profile is temporarily unavailable.");
            }

            var profile = await profileService.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);
            var verified = await verificationService.IsVerifiedAsync(user, cancellationToken).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new UserProfileResponse(
                profile?.DisplayName,
                profile?.Phone,
                profile?.Country,
                profile?.Region,
                profile?.Locality,
                user.Email,
                verified));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Saving your profile is temporarily unavailable.");
        }
    }

    private static async Task<IResult> RequireUserAsync(
        HttpContext context,
        UserManager<SwiftAidUser> userManager,
        Func<SwiftAidUser, Task<IResult>> handle)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to manage organization access.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            return user is null
                ? PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to manage organization access.")
                : await handle(user).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return PasswordError(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Organization access is temporarily unavailable.");
        }
    }

    private static async Task<IResult> GetMyMemberships(
        HttpContext context,
        UserManager<SwiftAidUser> userManager,
        IOrganizationMembershipService membershipService,
        CancellationToken cancellationToken) =>
        await RequireUserAsync(context, userManager, async user =>
        {
            var memberships = await membershipService.GetMineAsync(user.Id, cancellationToken).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new MembershipListResponse(memberships.Select(membership => new MembershipResponse(
                membership.OrganizationId,
                membership.OrganizationStatus,
                membership.MembershipStatus,
                membership.WorkspaceIds,
                membership.ValidFromUtc,
                membership.ExpiresAtUtc)).ToList()));
        }).ConfigureAwait(false);

    private static async Task<IResult> SearchOrganizations(
        HttpContext context,
        UserManager<SwiftAidUser> userManager,
        IOrganizationMembershipService membershipService,
        string? search,
        CancellationToken cancellationToken) =>
        await RequireUserAsync(context, userManager, async _ =>
        {
            var entries = await membershipService.SearchAsync(search, cancellationToken).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new OrganizationDirectoryResponse(entries.Select(entry => new OrganizationDirectoryEntryResponse(
                entry.OrganizationId,
                entry.Status)).ToList()));
        }).ConfigureAwait(false);

    private static async Task<IResult> GetMyMembershipRequests(
        HttpContext context,
        UserManager<SwiftAidUser> userManager,
        IOrganizationMembershipService membershipService,
        CancellationToken cancellationToken) =>
        await RequireUserAsync(context, userManager, async user =>
        {
            var requests = await membershipService.GetMyRequestsAsync(user.Id, cancellationToken).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new MembershipRequestListResponse(requests.Select(request => new MembershipRequestResponse(
                request.RequestId,
                request.OrganizationId,
                request.Status,
                request.Reason,
                request.CreatedAtUtc)).ToList()));
        }).ConfigureAwait(false);

    private static async Task<IResult> SubmitMembershipRequest(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        IOrganizationMembershipService membershipService,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The membership request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The membership request is invalid.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Choose an organization to request access.");
        }

        return await RequireUserAsync(context, userManager, async user =>
        {
            var submitted = await membershipService.RequestAsync(
                user.Id,
                bodyRead.Request.OrganizationId ?? string.Empty,
                bodyRead.Request.Reason,
                cancellationToken).ConfigureAwait(false);
            SetNoStore(context);
            return submitted.Status switch
            {
                MembershipRequestSubmitStatus.Submitted => Results.Ok(new MembershipRequestResponse(
                    submitted.Request!.RequestId,
                    submitted.Request.OrganizationId,
                    submitted.Request.Status,
                    submitted.Request.Reason,
                    submitted.Request.CreatedAtUtc)),
                MembershipRequestSubmitStatus.DuplicatePending => PasswordError(
                    context,
                    StatusCodes.Status409Conflict,
                    "You already have a pending request for this organization."),
                MembershipRequestSubmitStatus.AlreadyMember => PasswordError(
                    context,
                    StatusCodes.Status409Conflict,
                    "You are already a member of this organization."),
                MembershipRequestSubmitStatus.OrganizationUnavailable => PasswordError(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Choose an organization from the directory."),
                MembershipRequestSubmitStatus.Invalid => PasswordError(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Choose an organization to request access."),
                _ => PasswordError(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "Requesting access is temporarily unavailable.")
            };
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> GetNotificationPreferences(
        HttpContext context,
        UserManager<SwiftAidUser> userManager,
        INotificationPreferenceService preferenceService,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to view notification preferences.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to view notification preferences.");
            }

            var preferences = await preferenceService.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);
            SetNoStore(context);
            return Results.Ok(new NotificationPreferencesResponse(preferences.WorkReminders, preferences.SessionUpdates));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Notification preferences are temporarily unavailable.");
        }
    }

    private static async Task<IResult> SaveNotificationPreferences(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<SwiftAidUser> userManager,
        INotificationPreferenceService preferenceService,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The preferences request is invalid.");
        }
        catch (InvalidOperationException)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "The preferences request is invalid.");
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to save notification preferences.");
        }

        var bodyRead = await ReadPasswordRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (bodyRead.Status != PasswordBodyStatus.Valid || bodyRead.Request is null)
        {
            return PasswordError(context, StatusCodes.Status400BadRequest, "Choose your notification preferences.");
        }

        try
        {
            var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
            if (user is null)
            {
                return PasswordError(context, StatusCodes.Status401Unauthorized, "Sign in to save notification preferences.");
            }

            var saved = await preferenceService.SaveAsync(
                user.Id,
                new NotificationPreferenceData(
                    bodyRead.Request.WorkReminders,
                    bodyRead.Request.SessionUpdates),
                cancellationToken).ConfigureAwait(false);
            if (saved is null)
            {
                return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Saving preferences is temporarily unavailable.");
            }

            SetNoStore(context);
            return Results.Ok(new NotificationPreferencesResponse(saved.WorkReminders, saved.SessionUpdates));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PasswordError(context, StatusCodes.Status503ServiceUnavailable, "Saving preferences is temporarily unavailable.");
        }
    }

    private static IResult PasswordError(HttpContext context, int statusCode, string message)
    {
        SetNoStore(context);
        return Results.Json(new PasswordAuthError(message), statusCode: statusCode);
    }

    private static IResult ThrottledEmailOtp(HttpContext context, EmailVerificationOutcome outcome)
    {
        var retryAfterSeconds = Math.Max(0, outcome.RetryAfterSeconds);
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        SetNoStore(context);
        return Results.Json(
            new EmailOtpThrottledResponse(
                "A code was sent recently. Wait before requesting a new one.",
                retryAfterSeconds),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> AuthorizeClient(
        HttpContext context,
        SwiftAidDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest();
        if (request is null)
        {
            return Results.Unauthorized();
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            var returnUrl = await GetAuthorizationRequestReturnUrlAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (!IsLocalReturnUrl(returnUrl))
            {
                return Results.BadRequest(new { error = "The authorization request is invalid." });
            }

            return Results.LocalRedirect(QueryHelpers.AddQueryString("/sign-in", "returnUrl", returnUrl));
        }

        if (request.GetScopes().Any(scope =>
                !string.Equals(scope, OpenIddictConstants.Scopes.OpenId, StringComparison.Ordinal) &&
                !string.Equals(scope, OpenIddictConstants.Scopes.OfflineAccess, StringComparison.Ordinal)))
        {
            return Results.BadRequest(new { error = "The requested scope is not supported." });
        }

        ActorContext actor;
        try
        {
            actor = ActorContext.FromValidatedPrincipal(context.User);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow;
        var record = await dbContext.Actors.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ActorId == actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        var state = await dbContext.ActorAccessStates.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ActorId == actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        if (record is null || record.Status != PersistentActorStatus.Active ||
            record.ExpiresAtUtc is { } expiry && expiry <= now ||
            state is null || state.AccessEpoch != actor.AccessEpoch)
        {
            return Results.Unauthorized();
        }

        var principal = CreateIdentityPrincipal(actor.ActorId, actor.AccessEpoch);
        principal.SetScopes(request.GetScopes());
        SetIdentityClaimDestinations(principal);

        return Results.SignIn(
            principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ExchangeToken(
        HttpContext context,
        SwiftAidDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest();
        if (request is null ||
            (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType()))
        {
            return InvalidTokenRequest();
        }

        var token = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
            .ConfigureAwait(false);
        if (!token.Succeeded || token.Principal is null)
        {
            return InvalidTokenRequest();
        }

        ActorContext actor;
        try
        {
            actor = ActorContext.FromValidatedPrincipal(token.Principal);
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidTokenRequest();
        }

        var now = DateTimeOffset.UtcNow;
        var state = await dbContext.ActorAccessStates.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ActorId == actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        var record = await dbContext.Actors.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ActorId == actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        if (record is null || record.Status != PersistentActorStatus.Active ||
            record.ExpiresAtUtc is { } expiry && expiry <= now ||
            state is null || state.AccessEpoch != actor.AccessEpoch)
        {
            return InvalidTokenRequest();
        }

        // Do not copy cached organization/grant claims from the refresh principal. SwiftAid API
        // authorization reads current PostgreSQL membership and grants on every protected action.
        var principal = CreateIdentityPrincipal(actor.ActorId, state.AccessEpoch);
        var scopes = token.Principal.GetScopes()
            .Where(scope => scope is OpenIddictConstants.Scopes.OpenId or OpenIddictConstants.Scopes.OfflineAccess)
            .ToArray();
        if (!scopes.Contains(OpenIddictConstants.Scopes.OpenId, StringComparer.Ordinal))
        {
            return InvalidTokenRequest();
        }

        principal.SetScopes(scopes);
        SetIdentityClaimDestinations(principal);
        return Results.SignIn(
            principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static ClaimsPrincipal CreateIdentityPrincipal(string actorId, long accessEpoch)
    {
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, actorId));
        identity.AddClaim(new Claim(
            ActorContext.AccessEpochClaimType,
            accessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new ClaimsPrincipal(identity);
    }

    private static void SetIdentityClaimDestinations(ClaimsPrincipal principal) =>
        principal.SetDestinations(claim => claim.Type switch
        {
            OpenIddictConstants.Claims.Subject =>
                [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
            ActorContext.AccessEpochClaimType => [OpenIddictConstants.Destinations.AccessToken],
            _ => []
        });

    private static IResult InvalidTokenRequest() => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private static async Task<string> GetAuthorizationRequestReturnUrlAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            query = new Microsoft.AspNetCore.Http.QueryCollection(form.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal));
        }

        return string.Concat(
            context.Request.PathBase,
            "/connect/authorize",
            QueryString.Create(query));
    }

    private static IResult EndSession(HttpContext context)
    {
        var request = context.GetOpenIddictServerRequest();
        if (request is null)
        {
            return Results.BadRequest(new { error = "The sign-out request is invalid." });
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = request.PostLogoutRedirectUri
        };
        return Results.SignOut(
            properties,
            [IdentityConstants.ApplicationScheme, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static bool TryGetVerifiedProvider(
        FederationOptions options,
        string providerId,
        out string scheme,
        out string issuer)
    {
        scheme = string.Empty;
        issuer = string.Empty;
        if (!options.Enabled)
        {
            return false;
        }

        if (string.Equals(providerId, FederationSetup.GoogleProviderId, StringComparison.Ordinal) &&
            options.Google.Enabled && options.Google.CallbackVerified)
        {
            scheme = FederationSetup.GoogleScheme;
            issuer = FederationSetup.GoogleIssuer;
            return true;
        }

        if (string.Equals(providerId, FederationSetup.FacebookProviderId, StringComparison.Ordinal) &&
            options.Facebook.Enabled && options.Facebook.CallbackVerified)
        {
            scheme = FederationSetup.FacebookScheme;
            issuer = FederationSetup.FacebookIssuer;
            return true;
        }

        var organization = options.OrganizationProviders.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderId, providerId, StringComparison.Ordinal) &&
            candidate.CallbackVerified);
        if (organization is null)
        {
            return false;
        }

        scheme = FederationSetup.GetOrganizationScheme(providerId);
        issuer = organization.Issuer;
        return true;
    }

    private static bool TryGetCurrentActor(HttpContext context, out ActorContext actor)
    {
        actor = null!;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        try
        {
            actor = ActorContext.FromValidatedPrincipal(context.User);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> IsCurrentActorAsync(
        ActorContext actor,
        SwiftAidDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.Actors.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ActorId == actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        var state = await dbContext.ActorAccessStates.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ActorId == actor.ActorId, cancellationToken)
            .ConfigureAwait(false);

        return record is not null && record.Status == PersistentActorStatus.Active &&
            (record.ExpiresAtUtc is null || record.ExpiresAtUtc > timeProvider.GetUtcNow()) &&
            state is not null && state.AccessEpoch == actor.AccessEpoch;
    }

    private static AuthenticationProperties CreateRecentAuthenticationProperties(
        DateTimeOffset authenticatedAt,
        string provider)
    {
        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            IssuedUtc = authenticatedAt
        };
        properties.Items[ReauthenticatedAtKey] = authenticatedAt.ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        properties.Items[ReauthenticatedProviderKey] = provider;
        return properties;
    }

    private static async Task<bool> HasRecentAuthenticationAsync(
        HttpContext context,
        TimeProvider timeProvider)
    {
        var ticket = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        if (!ticket.Succeeded || ticket.Properties is null ||
            !ticket.Properties.Items.TryGetValue(ReauthenticatedAtKey, out var value) ||
            !ticket.Properties.Items.TryGetValue(ReauthenticatedProviderKey, out var provider) ||
            string.IsNullOrWhiteSpace(provider) ||
            !long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var timestamp))
        {
            return false;
        }

        DateTimeOffset authenticatedAt;
        try
        {
            authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var age = timeProvider.GetUtcNow() - authenticatedAt;
        return age >= TimeSpan.FromSeconds(-30) && age <= RecentAuthenticationWindow;
    }

    private static bool IsLocalReturnUrl(string? value) =>
        value is { Length: > 0 and <= 2048 } &&
        value[0] == '/' &&
        (value.Length == 1 || value[1] is not ('/' or '\\')) &&
        !value.Contains('\\') &&
        !value.Contains('\r') &&
        !value.Contains('\n');

    private sealed record PasswordPolicyResponse(
        int MinimumLength,
        bool RequireDigit,
        bool RequireLowercase,
        bool RequireUppercase,
        bool RequireSymbol,
        int RequiredUniqueChars);

    private sealed record PasswordAuthResponse(string Email);

    private sealed record PasswordResetRequestResponse(string Message);

    private sealed record PasswordAuthError(string Error);

    private sealed record EmailVerificationStatusResponse(bool Verified, string? Email);

    private sealed record EmailOtpRequestResponse(string Message, int RetryAfterSeconds);

    private sealed record EmailOtpThrottledResponse(string Error, int RetryAfterSeconds);

    private sealed record EmailOtpConfirmResponse(bool Verified);

    private sealed record UserProfileResponse(
        string? DisplayName,
        string? Phone,
        string? Country,
        string? Region,
        string? Locality,
        string? Email,
        bool EmailVerified);

    private sealed record MembershipResponse(
        string OrganizationId,
        string OrganizationStatus,
        string MembershipStatus,
        IReadOnlyList<string> WorkspaceIds,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset? ExpiresAtUtc);

    private sealed record MembershipListResponse(IReadOnlyList<MembershipResponse> Memberships);

    private sealed record OrganizationDirectoryEntryResponse(string OrganizationId, string Status);

    private sealed record OrganizationDirectoryResponse(IReadOnlyList<OrganizationDirectoryEntryResponse> Organizations);

    private sealed record MembershipRequestResponse(
        Guid RequestId,
        string OrganizationId,
        string Status,
        string? Reason,
        DateTimeOffset CreatedAtUtc);

    private sealed record MembershipRequestListResponse(IReadOnlyList<MembershipRequestResponse> Requests);

    private sealed record NotificationPreferencesResponse(bool WorkReminders, bool SessionUpdates);

    private sealed class PasswordRequest
    {
        public PasswordRequest()
        {
        }

        public string? Email { get; set; }

        public string? Password { get; set; }

        public bool RememberMe { get; set; }

        public string? CurrentPassword { get; set; }

        public string? NewPassword { get; set; }

        public string? UserId { get; set; }

        public string? Code { get; set; }

        public string? DisplayName { get; set; }

        public string? Phone { get; set; }

        public string? Country { get; set; }

        public string? Region { get; set; }

        public string? Locality { get; set; }

        public string? OrganizationId { get; set; }

        public string? Reason { get; set; }

        public bool WorkReminders { get; set; } = true;

        public bool SessionUpdates { get; set; } = true;
    }

    private enum PasswordBodyStatus
    {
        Valid,
        Invalid,
        Oversized
    }

    private sealed record PasswordBodyReadResult(
        PasswordBodyStatus Status,
        PasswordRequest? Request = null);

    private sealed record ProviderAvailability(string Id, string DisplayName, bool Available);

    private sealed record AntiforgeryTokenResponse(string RequestToken, string HeaderName);

    private sealed record FacebookWebLoginOptionsResponse(
        bool Available,
        string? AppId = null,
        string? GraphApiVersion = null);

    private sealed record FacebookWebLoginExchangeResponse(string RedirectUrl);

    private sealed record FacebookWebLoginError(string Code);

    private sealed class FacebookWebLoginExchangeRequest
    {
        public FacebookWebLoginExchangeRequest()
        {
        }

        public string AccessToken { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
    }

    private enum FacebookExchangeBodyStatus
    {
        Valid,
        Invalid,
        Oversized
    }

    private sealed record FacebookExchangeBodyReadResult(
        FacebookExchangeBodyStatus Status,
        FacebookWebLoginExchangeRequest? Request = null);
}
