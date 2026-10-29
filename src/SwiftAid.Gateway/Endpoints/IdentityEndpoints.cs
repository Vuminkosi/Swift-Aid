using System.Security.Claims;
using System.Text.Json;
using System.Net.Http.Headers;
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
            group.MapPost("/identities/{providerId}/link", LinkExternalIdentity);
            group.MapGet("/identities/link/callback", CompleteExternalIdentityLink);
            group.MapPost("/identities/{providerId}/unlink", UnlinkExternalIdentity);
            group.MapPost("/logout", SignOutSwiftAid);
        }

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
