using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

public sealed class GoogleOAuthChallengeOriginTests
{
    [Fact]
    public async Task ChallengeUsesCanonicalAllowedHostInGoogleRedirectUri()
    {
        await using var host = await GoogleChallengeTestHost.StartAsync();

        using var response = await host.SendChallengeAsync("swiftaid.online");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal("accounts.google.com", location.Host);
        Assert.Equal(
            "https://swiftaid.online/google/callback",
            QueryHelpers.ParseQuery(location.Query)["redirect_uri"].ToString());
    }

    [Fact]
    public async Task HostFilteringRejectsHostileHostEvenWhenForwardedHostIsCanonical()
    {
        await using var host = await GoogleChallengeTestHost.StartAsync();

        using var response = await host.SendChallengeAsync(
            "attacker.example",
            forwardedHost: "swiftaid.online");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task ForwardedHostCannotChangeCallbackForCanonicalRequestHost()
    {
        await using var host = await GoogleChallengeTestHost.StartAsync();

        using var response = await host.SendChallengeAsync(
            "swiftaid.online",
            forwardedHost: "attacker.example");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal("accounts.google.com", location.Host);
        Assert.Equal(
            "https://swiftaid.online/google/callback",
            QueryHelpers.ParseQuery(location.Query)["redirect_uri"].ToString());
    }

    private sealed class GoogleChallengeTestHost : IAsyncDisposable
    {
        private const string AntiforgeryHeader = "X-CSRF-TOKEN";
        private readonly WebApplication _application;
        private readonly IAntiforgery _antiforgery;

        private GoogleChallengeTestHost(WebApplication application, HttpClient client)
        {
            _application = application;
            Client = client;
            _antiforgery = application.Services.GetRequiredService<IAntiforgery>();
        }

        private HttpClient Client { get; }

        public static async Task<GoogleChallengeTestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration["AllowedHosts"] = "swiftaid.online";
            builder.Services.AddDbContext<SwiftAidDbContext>(options =>
            {
                options.UseNpgsql(
                    "Host=127.0.0.1;Port=1;Database=swiftaid_endpoint_tests;Username=unused;Password=unused;Timeout=1");
                options.UseOpenIddict();
            });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddIdentityCore<SwiftAidUser>(options =>
                    options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<SwiftAidDbContext>()
                .AddSignInManager();
            builder.Services.AddSingleton(new ProbeIdentityAuthenticationState(
                "unused-actor",
                null,
                FederationSetup.GoogleScheme));
            builder.Services.AddAntiforgery(options =>
            {
                options.HeaderName = AntiforgeryHeader;
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
            });

            var federationOptions = new FederationOptions
            {
                Enabled = true,
                Google = new SocialProviderOptions
                {
                    Enabled = true,
                    CallbackVerified = true,
                    CallbackPath = "/google/callback",
                    ClientId = "google-test-client",
                    ClientSecret = "test-only-not-a-secret"
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
            var authentication = builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, ProbeIdentityAuthenticationHandler>(
                    IdentityConstants.ApplicationScheme,
                    _ => { })
                .AddScheme<AuthenticationSchemeOptions, ProbeIdentityAuthenticationHandler>(
                    IdentityConstants.ExternalScheme,
                    _ => { });
            FederationSetup.AddConfiguredProviders(authentication, federationOptions);

            var application = builder.Build();
            application.UseAuthentication();
            application.MapSwiftAidIdentityEndpoints(
                federationOptions,
                new IdentityServerOptions());
            await application.StartAsync().ConfigureAwait(false);

            return new GoogleChallengeTestHost(application, application.GetTestClient());
        }

        public async Task<HttpResponseMessage> SendChallengeAsync(
            string host,
            string? forwardedHost = null)
        {
            var antiforgeryContext = new DefaultHttpContext
            {
                RequestServices = _application.Services
            };
            antiforgeryContext.Request.Scheme = "https";
            antiforgeryContext.Request.Host = new HostString(host);
            var antiforgeryTokens = _antiforgery.GetAndStoreTokens(antiforgeryContext);
            var antiforgeryCookie = antiforgeryContext.Response.Headers.SetCookie
                .ToString()
                .Split(';', 2)[0];

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri($"https://{host}/auth/google/challenge?returnUrl=%2Faccount"));
            request.Headers.TryAddWithoutValidation(AntiforgeryHeader, antiforgeryTokens.RequestToken);
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
            if (!string.IsNullOrWhiteSpace(forwardedHost))
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost);
            }

            return await Client.SendAsync(request).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }
}




