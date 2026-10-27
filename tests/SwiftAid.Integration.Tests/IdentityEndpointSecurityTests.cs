using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Exercises the public identity endpoint routes with local authentication handlers.
/// These tests verify server-side rejection gates and do not contact an identity provider.
/// </summary>
public sealed class IdentityEndpointSecurityTests
{
    [Fact]
    public async Task ProviderChallenge_WithoutAntiforgeryToken_IsRejectedBeforeProviderChallenge()
    {
        await using var host = await IdentityEndpointTestHost.StartAsync(callbackVerified: true);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/auth/google/challenge?returnUrl=%2Faccount");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.ProviderChallenges.Count);
    }

    [Fact]
    public async Task ProviderChallenge_WithInvalidAntiforgeryToken_IsRejectedBeforeProviderChallenge()
    {
        await using var host = await IdentityEndpointTestHost.StartAsync(callbackVerified: true);
        _ = await host.GetAntiforgeryAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/auth/google/challenge?returnUrl=%2Faccount");
        host.AddAntiforgeryCookie(request);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", "invalid-token");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.ProviderChallenges.Count);
    }

    [Fact]
    public async Task ReauthenticateProvider_WithoutAntiforgeryToken_IsRejectedBeforeProviderChallenge()
    {
        await using var host = await IdentityEndpointTestHost.StartAsync(callbackVerified: true);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/auth/reauthenticate/google?returnUrl=%2Faccount");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.ProviderChallenges.Count);
    }

    [Fact]
    public async Task ReauthenticateProvider_WithInvalidAntiforgeryToken_IsRejectedBeforeProviderChallenge()
    {
        await using var host = await IdentityEndpointTestHost.StartAsync(callbackVerified: true);
        _ = await host.GetAntiforgeryAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/auth/reauthenticate/google?returnUrl=%2Faccount");
        host.AddAntiforgeryCookie(request);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", "invalid-token");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.ProviderChallenges.Count);
    }

    [Theory]
    [InlineData("//attacker.example.test/path")]
    [InlineData("https://attacker.example.test/path")]
    [InlineData("/\\attacker.example.test/path")]
    public async Task ProviderChallenge_WithUnsafeReturnUrl_IsRejected(string returnUrl)
    {
        await using var host = await IdentityEndpointTestHost.StartAsync(callbackVerified: true);
        var antiforgery = await host.GetAntiforgeryAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/auth/google/challenge?returnUrl={Uri.EscapeDataString(returnUrl)}");
        host.AddAntiforgery(request, antiforgery);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.ProviderChallenges.Count);
    }

    [Fact]
    public async Task ProviderChallenge_WhenCallbackIsNotVerified_IsRejected()
    {
        await using var host = await IdentityEndpointTestHost.StartAsync(callbackVerified: false);
        var antiforgery = await host.GetAntiforgeryAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/auth/google/challenge?returnUrl=%2Faccount");
        host.AddAntiforgery(request, antiforgery);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.ProviderChallenges.Count);
    }

    [Fact]
    public async Task IdentityLink_WithoutAntiforgeryToken_IsRejectedBeforeIdentityLookup()
    {
        await using var host = await IdentityEndpointTestHost.StartAsync(callbackVerified: true);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/auth/identities/google/link?returnUrl=%2Faccount");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.ProviderChallenges.Count);
    }

    [Fact]
    public async Task IdentityUnlink_WithoutAntiforgeryToken_IsRejectedBeforeMappingStore()
    {
        await using var host = await IdentityEndpointTestHost.StartAsync(callbackVerified: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/identities/google/unlink");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.MappingStore.UnlinkCalls);
    }

    [IdentityPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityUnlink_WithoutRecentAuthentication_IsRejectedBeforeMappingStore(
        bool useStaleMarker)
    {
        DateTimeOffset? authenticatedAt = useStaleMarker ? DateTimeOffset.UtcNow.AddMinutes(-6) : null;
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(
            actor.ActorId,
            authenticatedAt,
            FederationSetup.GoogleScheme);
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);
        var antiforgery = await host.GetAntiforgeryAsync(authenticated: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/identities/google/unlink");
        host.AddAntiforgery(request, antiforgery);
        host.AddIdentityCookies(request);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, host.MappingStore.UnlinkCalls);
        Assert.Null(host.MappingStore.LastUnlinkActorId);
    }

    [IdentityPostgresFact]
    public async Task IdentityUnlink_WithRecentAuthentication_ReachesMappingStore()
    {
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(
            actor.ActorId,
            DateTimeOffset.UtcNow,
            FederationSetup.GoogleScheme);
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);
        host.MappingStore.UnlinkResult = ExternalIdentityUnlinkStatus.NotLinked;
        var antiforgery = await host.GetAntiforgeryAsync(authenticated: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/identities/google/unlink");
        host.AddAntiforgery(request, antiforgery);
        host.AddIdentityCookies(request);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, host.MappingStore.UnlinkCalls);
        Assert.Equal(actor.ActorId, host.MappingStore.LastUnlinkActorId);
        Assert.Equal("google", host.MappingStore.LastUnlinkProvider);
    }

    [IdentityPostgresFact]
    public async Task ReauthenticateCallback_WithoutExternalProof_DoesNotMintMarkerOrUnlink()
    {
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(actor.ActorId, null, FederationSetup.GoogleScheme);
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);

        using var callback = await CompleteReauthenticationAsync(host, includeExternal: false);
        Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        Assert.Equal(0, host.MappingStore.ResolveExistingCalls);
        Assert.Null(authState.ReauthenticatedAtUtc);
        Assert.Equal(0, authState.SignInCalls);
        Assert.Equal(1, authState.ExternalSignOutCalls);

        using var unlink = await AttemptUnlinkAsync(host);
        Assert.Equal(HttpStatusCode.Conflict, unlink.StatusCode);
        Assert.Equal(0, host.MappingStore.UnlinkCalls);
    }

    [IdentityPostgresFact]
    public async Task ReauthenticateCallback_WithProviderSchemeMismatch_DoesNotMintMarkerOrUnlink()
    {
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(actor.ActorId, null, "not-google");
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);

        using var callback = await CompleteReauthenticationAsync(host);
        Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        Assert.Equal(0, host.MappingStore.ResolveExistingCalls);
        Assert.Null(authState.ReauthenticatedAtUtc);
        Assert.Equal(0, authState.SignInCalls);
        Assert.Equal(1, authState.ExternalSignOutCalls);

        using var unlink = await AttemptUnlinkAsync(host);
        Assert.Equal(HttpStatusCode.Conflict, unlink.StatusCode);
        Assert.Equal(0, host.MappingStore.UnlinkCalls);
    }

    [IdentityPostgresFact]
    public async Task ReauthenticateCallback_WhenProofMapsToAnotherActor_DoesNotMintMarkerOrUnlink()
    {
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(actor.ActorId, null, FederationSetup.GoogleScheme);
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);
        host.MappingStore.ResolveExistingResult = new ExternalIdentityResolution(
            ExternalIdentityResolutionStatus.Resolved,
            "different-actor");

        using var callback = await CompleteReauthenticationAsync(host);
        Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        Assert.Equal(1, host.MappingStore.ResolveExistingCalls);
        Assert.Null(authState.ReauthenticatedAtUtc);
        Assert.Equal(0, authState.SignInCalls);
        Assert.Equal(1, authState.ExternalSignOutCalls);

        using var unlink = await AttemptUnlinkAsync(host);
        Assert.Equal(HttpStatusCode.Conflict, unlink.StatusCode);
        Assert.Equal(0, host.MappingStore.UnlinkCalls);
    }

    [IdentityPostgresFact]
    public async Task ReauthenticateCallback_WithSameActorProof_MintsMarkerAndAllowsOneUnlinkDispatch()
    {
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(actor.ActorId, null, FederationSetup.GoogleScheme);
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);
        host.MappingStore.ResolveExistingResult = new ExternalIdentityResolution(
            ExternalIdentityResolutionStatus.Resolved,
            actor.ActorId);
        host.MappingStore.UnlinkResult = ExternalIdentityUnlinkStatus.NotLinked;

        using var callback = await CompleteReauthenticationAsync(host);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(1, authState.SignInCalls);
        Assert.Equal(1, authState.ExternalSignOutCalls);
        Assert.NotNull(authState.ReauthenticatedAtUtc);
        Assert.Equal(FederationSetup.GoogleProviderId, authState.ReauthenticatedProvider);
        Assert.Equal(1, host.MappingStore.ResolveExistingCalls);

        using var unlink = await AttemptUnlinkAsync(host);
        Assert.Equal(HttpStatusCode.NotFound, unlink.StatusCode);
        Assert.Equal(1, host.MappingStore.UnlinkCalls);
        Assert.Equal(actor.ActorId, host.MappingStore.LastUnlinkActorId);
        Assert.Equal(FederationSetup.GoogleProviderId, host.MappingStore.LastUnlinkProvider);
    }

    [IdentityPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkCallback_WithoutRecentAuthentication_IsRejected(bool useStaleMarker)
    {
        DateTimeOffset? authenticatedAt = useStaleMarker ? DateTimeOffset.UtcNow.AddMinutes(-6) : null;
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(actor.ActorId, authenticatedAt, "google");
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/identities/link/callback?provider=google&returnUrl=%2Faccount");
        host.AddIdentityCookies(request, includeExternal: true);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.MappingStore.LinkCalls);
    }

    [IdentityPostgresFact]
    public async Task LinkCallback_WhenExternalLoginProviderDoesNotMatchRoute_IsRejected()
    {
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(
            actor.ActorId,
            DateTimeOffset.UtcNow,
            "not-google");
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/identities/link/callback?provider=google&returnUrl=%2Faccount");
        host.AddIdentityCookies(request, includeExternal: true);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.MappingStore.LinkCalls);
    }

    [IdentityPostgresFact]
    public async Task LinkCallback_WhenExternalAccountBelongsToAnotherActor_ReturnsConflictForCurrentActor()
    {
        await using var actor = await IdentityEndpointActor.CreateAsync();
        var authState = new ProbeIdentityAuthenticationState(
            actor.ActorId,
            DateTimeOffset.UtcNow,
            FederationSetup.GoogleScheme);
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState);
        host.MappingStore.LinkResult = ExternalIdentityLinkStatus.Conflict;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/identities/link/callback?provider=google&returnUrl=%2Faccount");
        host.AddIdentityCookies(request, includeExternal: true);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, host.MappingStore.LinkCalls);
        Assert.Equal(actor.ActorId, host.MappingStore.LastLinkActorId);
    }

    [IdentityPostgresTheory]
    [InlineData(PersistentActorStatus.Suspended, 7)]
    [InlineData(PersistentActorStatus.Active, 8)]
    public async Task Authorize_WhenActorIsInactiveOrAccessEpochIsStale_IsRejected(
        PersistentActorStatus actorStatus,
        long storedAccessEpoch)
    {
        await using var actor = await IdentityEndpointActor.CreateAsync(actorStatus, storedAccessEpoch);
        var authState = new ProbeIdentityAuthenticationState(actor.ActorId, null, "google");
        await using var host = await IdentityEndpointTestHost.StartAsync(
            callbackVerified: true,
            actor.ConnectionString,
            authState,
            identityServerEnabled: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/authorize?scope=openid");
        host.AddIdentityCookies(request);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> CompleteReauthenticationAsync(
        IdentityEndpointTestHost host,
        bool includeExternal = true)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/reauthenticate/callback?provider=google&returnUrl=%2Faccount");
        host.AddIdentityCookies(request, includeExternal);
        return await host.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> AttemptUnlinkAsync(IdentityEndpointTestHost host)
    {
        var antiforgery = await host.GetAntiforgeryAsync(authenticated: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/identities/google/unlink");
        host.AddAntiforgery(request, antiforgery);
        host.AddIdentityCookies(request);
        return await host.Client.SendAsync(request);
    }
}

internal sealed class IdentityEndpointTestHost : IAsyncDisposable
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";
    private readonly WebApplication _application;
    private readonly IAntiforgery _antiforgery;
    private string? _antiforgeryCookie;

    private IdentityEndpointTestHost(
        WebApplication application,
        HttpClient client,
        ProbeAuthenticationCounter providerChallenges,
        RejectingExternalIdentityMappingStore mappingStore)
    {
        _application = application;
        Client = client;
        ProviderChallenges = providerChallenges;
        MappingStore = mappingStore;
        AuthenticationState = application.Services.GetRequiredService<ProbeIdentityAuthenticationState>();
        _antiforgery = application.Services.GetRequiredService<IAntiforgery>();
    }

    public HttpClient Client { get; }

    public ProbeAuthenticationCounter ProviderChallenges { get; }

    public RejectingExternalIdentityMappingStore MappingStore { get; }

    public ProbeIdentityAuthenticationState AuthenticationState { get; }

    public static async Task<IdentityEndpointTestHost> StartAsync(
        bool callbackVerified,
        string? connectionString = null,
        ProbeIdentityAuthenticationState? authenticationState = null,
        bool identityServerEnabled = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeader;
            options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        });
        builder.Services.AddDbContext<SwiftAidDbContext>(options =>
        {
            options.UseNpgsql(connectionString ??
                "Host=127.0.0.1;Port=1;Database=swiftaid_endpoint_tests;Username=unused;Password=unused;Timeout=1");
            options.UseOpenIddict();
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ProbeAuthenticationCounter>();
        var identityAuthenticationState = authenticationState ?? new ProbeIdentityAuthenticationState(
            "unused-actor",
            null,
            FederationSetup.GoogleScheme);
        builder.Services.AddSingleton(identityAuthenticationState);
        builder.Services.AddIdentityCore<SwiftAidUser>(options =>
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<SwiftAidDbContext>()
            .AddSignInManager();
        var federationOptions = new FederationOptions
        {
            Enabled = true,
            Google = new SocialProviderOptions
            {
                Enabled = true,
                CallbackVerified = callbackVerified
            }
        };
        builder.Services.AddSingleton(federationOptions);
        builder.Services.AddSingleton<RejectingExternalIdentityMappingStore>();
        builder.Services.AddSingleton<IExternalIdentityMappingStore>(services =>
            services.GetRequiredService<RejectingExternalIdentityMappingStore>());
        builder.Services.AddSingleton<IExternalIdentityResolver>(services => new ExternalIdentityResolver(
            federationOptions,
            new OrganizationSsoRegistry(federationOptions),
            services.GetRequiredService<IExternalIdentityMappingStore>()));
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, ProbeIdentityAuthenticationHandler>(
                IdentityConstants.ApplicationScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, ProbeIdentityAuthenticationHandler>(
                IdentityConstants.ExternalScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, ProbeAuthenticationHandler>(
                FederationSetup.GoogleScheme,
                _ => { });

        var application = builder.Build();
        application.UseAuthentication();
        application.Use(async (context, next) =>
        {
            if (identityServerEnabled && context.Request.Path == "/connect/authorize")
            {
                var transaction = new OpenIddictServerTransaction
                {
                    Request = new OpenIddictRequest { Scope = OpenIddictConstants.Scopes.OpenId }
                };
                context.Features.Set(new OpenIddictServerAspNetCoreFeature { Transaction = transaction });
            }

            await next().ConfigureAwait(false);
        });
        application.MapSwiftAidIdentityEndpoints(
            federationOptions,
            new IdentityServerOptions { Enabled = identityServerEnabled });
        await application.StartAsync().ConfigureAwait(false);

        return new IdentityEndpointTestHost(
            application,
            application.GetTestClient(),
            application.Services.GetRequiredService<ProbeAuthenticationCounter>(),
            application.Services.GetRequiredService<RejectingExternalIdentityMappingStore>());
    }

    public Task<string> GetAntiforgeryAsync(bool authenticated = false)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = _application.Services
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        if (authenticated)
        {
            var state = _application.Services.GetRequiredService<ProbeIdentityAuthenticationState>();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, state.ActorId),
                new Claim(SwiftAid.Application.Authorization.ActorContext.AccessEpochClaimType, "7")
            ],
            IdentityConstants.ApplicationScheme));
        }

        var tokens = _antiforgery.GetAndStoreTokens(context);
        _antiforgeryCookie = context.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        return Task.FromResult(tokens.RequestToken
            ?? throw new InvalidOperationException("The antiforgery request token was not created."));
    }

    public void AddAntiforgery(HttpRequestMessage request, string requestToken)
    {
        if (string.IsNullOrWhiteSpace(_antiforgeryCookie))
        {
            throw new InvalidOperationException("The antiforgery cookie was not created.");
        }

        AddAntiforgeryCookie(request);
        request.Headers.TryAddWithoutValidation(AntiforgeryHeader, requestToken);
    }

    public void AddAntiforgeryCookie(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_antiforgeryCookie))
        {
            throw new InvalidOperationException("The antiforgery cookie was not created.");
        }

        request.Headers.TryAddWithoutValidation("Cookie", _antiforgeryCookie);
    }

    public void AddIdentityCookies(HttpRequestMessage request, bool includeExternal = false)
    {
        var cookies = "swiftaid-endpoint-app=authenticated";
        if (includeExternal)
        {
            cookies += "; swiftaid-endpoint-external=authenticated";
        }

        if (request.Headers.TryGetValues("Cookie", out var existingCookies))
        {
            cookies = $"{string.Join("; ", existingCookies)}; {cookies}";
            request.Headers.Remove("Cookie");
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookies);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class ProbeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    ProbeAuthenticationCounter counter)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        counter.Count++;
        Response.StatusCode = StatusCodes.Status418ImATeapot;
        return Task.CompletedTask;
    }
}

internal sealed class ProbeAuthenticationCounter
{
    public int Count { get; set; }
}

internal sealed record ProbeIdentityAuthenticationState(
    string ActorId,
    DateTimeOffset? InitialReauthenticatedAtUtc,
    string ExternalLoginProvider)
{
    public DateTimeOffset? ReauthenticatedAtUtc { get; set; } = InitialReauthenticatedAtUtc;

    public string? ReauthenticatedProvider { get; set; }

    public int SignInCalls { get; set; }

    public int ExternalSignOutCalls { get; set; }
}

internal sealed class ProbeIdentityAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    ProbeIdentityAuthenticationState state)
    : SignInAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookieName = string.Equals(
            Scheme.Name,
            IdentityConstants.ApplicationScheme,
            StringComparison.Ordinal)
            ? "swiftaid-endpoint-app"
            : "swiftaid-endpoint-external";
        if (!Request.Cookies.TryGetValue(cookieName, out var value) ||
            !string.Equals(value, "authenticated", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var isApplicationCookie = string.Equals(
            Scheme.Name,
            IdentityConstants.ApplicationScheme,
            StringComparison.Ordinal);
        var claims = isApplicationCookie
            ? new[]
            {
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier,
                    state.ActorId),
                new System.Security.Claims.Claim(
                    SwiftAid.Application.Authorization.ActorContext.AccessEpochClaimType,
                    "7")
            }
            :
            [new System.Security.Claims.Claim(
                System.Security.Claims.ClaimTypes.NameIdentifier,
                "external-subject")];
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, Scheme.Name));
        var properties = new AuthenticationProperties();
        if (isApplicationCookie)
        {
            if (state.ReauthenticatedAtUtc is { } reauthenticatedAtUtc)
            {
                properties.Items["swiftaid:reauthenticated-at-utc"] =
                    reauthenticatedAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                properties.Items["swiftaid:reauthenticated-provider"] =
                    state.ReauthenticatedProvider ?? FederationSetup.GoogleProviderId;
            }
        }
        else
        {
            properties.Items["LoginProvider"] = state.ExternalLoginProvider;
        }

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, properties, Scheme.Name)));
    }

    protected override Task HandleSignInAsync(
        System.Security.Claims.ClaimsPrincipal user,
        AuthenticationProperties? properties)
    {
        if (string.Equals(Scheme.Name, IdentityConstants.ApplicationScheme, StringComparison.Ordinal))
        {
            state.SignInCalls++;
            var signInProperties = properties ?? new AuthenticationProperties();
            if (signInProperties.Items.TryGetValue("swiftaid:reauthenticated-at-utc", out var timestamp) &&
                long.TryParse(timestamp, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var unixSeconds))
            {
                state.ReauthenticatedAtUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }

            signInProperties.Items.TryGetValue("swiftaid:reauthenticated-provider", out var provider);
            state.ReauthenticatedProvider = provider;
        }

        return Task.CompletedTask;
    }

    protected override Task HandleSignOutAsync(AuthenticationProperties? properties)
    {
        if (string.Equals(Scheme.Name, IdentityConstants.ExternalScheme, StringComparison.Ordinal))
        {
            state.ExternalSignOutCalls++;
        }

        return Task.CompletedTask;
    }
}

internal sealed class IdentityEndpointActor : IAsyncDisposable
{
    private readonly ExternalIdentityPostgresFixture _fixture;

    private IdentityEndpointActor(
        ExternalIdentityPostgresFixture fixture,
        string actorId)
    {
        _fixture = fixture;
        ActorId = actorId;
    }

    public string ActorId { get; }

    public string ConnectionString => _fixture.ConnectionString;

    public static async Task<IdentityEndpointActor> CreateAsync(
        PersistentActorStatus status = PersistentActorStatus.Active,
        long accessEpoch = 7)
    {
        var fixture = await ExternalIdentityPostgresFixture.CreateAsync().ConfigureAwait(false);
        var actorId = $"identity-endpoint-test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = fixture.CreateDbContext();
        dbContext.Actors.Add(new ActorEntity
        {
            ActorId = actorId,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        dbContext.ActorAccessStates.Add(new ActorAccessStateEntity
        {
            ActorId = actorId,
            AccessEpoch = accessEpoch,
            Version = 1,
            UpdatedAtUtc = now
        });
        dbContext.Users.Add(new SwiftAidUser
        {
            Id = actorId,
            UserName = actorId,
            NormalizedUserName = actorId.ToUpperInvariant()
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return new IdentityEndpointActor(fixture, actorId);
    }

    public async ValueTask DisposeAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        await dbContext.ActorAccessStates
            .Where(value => value.ActorId == ActorId)
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
        await dbContext.Users
            .Where(value => value.Id == ActorId)
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
        await dbContext.Actors
            .Where(value => value.ActorId == ActorId)
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
        await _fixture.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class RejectingExternalIdentityMappingStore : IExternalIdentityMappingStore
{
    public int ResolveExistingCalls { get; private set; }

    public int LinkCalls { get; private set; }

    public int UnlinkCalls { get; private set; }

    public string? LastLinkActorId { get; private set; }

    public string? LastUnlinkActorId { get; private set; }

    public string? LastUnlinkProvider { get; private set; }

    public ExternalIdentityLinkStatus LinkResult { get; set; } = ExternalIdentityLinkStatus.Rejected;

    public ExternalIdentityResolution ResolveExistingResult { get; set; } =
        new(ExternalIdentityResolutionStatus.Rejected);

    public ExternalIdentityUnlinkStatus UnlinkResult { get; set; } = ExternalIdentityUnlinkStatus.Rejected;

    public ValueTask<ExternalIdentityResolution> ResolveOrCreateOrdinaryActorAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected));

    public ValueTask<ExternalIdentityResolution> ResolveExistingAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        ResolveExistingCalls++;
        return ValueTask.FromResult(ResolveExistingResult);
    }

    public ValueTask<ExternalIdentityLinkStatus> LinkAsync(
        string actorId,
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        LinkCalls++;
        LastLinkActorId = actorId;
        return ValueTask.FromResult(LinkResult);
    }

    public ValueTask<ExternalIdentityUnlinkStatus> UnlinkProviderAsync(
        string actorId,
        string provider,
        CancellationToken cancellationToken)
    {
        UnlinkCalls++;
        LastUnlinkActorId = actorId;
        LastUnlinkProvider = provider;
        return ValueTask.FromResult(UnlinkResult);
    }
}
