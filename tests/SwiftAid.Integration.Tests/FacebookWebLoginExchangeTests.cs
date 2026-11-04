using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;
using StackExchange.Redis;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Verifies the browser-token exchange independently from the existing server-managed OAuth callback.
/// Provider calls use a fake validator here; the fixed-host TLS canary is a separate opt-in Docker test.
/// </summary>
public sealed class FacebookWebLoginExchangeTests
{
    private const string ExchangePath = "/auth/facebook/web/exchange";
    private const string OptionsPath = "/auth/facebook/web/options";
    internal const string CsrfHeader = "X-CSRF-TOKEN";

    [Fact]
    public void FederationOptions_WebLoginRequiresStrongBase64HmacKeyOnlyWhenEnabled()
    {
        var validator = new ProviderOptionsValidator();
        var enabledWithoutKey = FacebookWebLoginTestHost.CreateReadyOptions();
        enabledWithoutKey.FacebookWebLogin.RateLimitHmacKeyBase64 = string.Empty;
        Assert.True(validator.Validate(null, enabledWithoutKey).Failed);

        var enabledWithShortKey = FacebookWebLoginTestHost.CreateReadyOptions();
        enabledWithShortKey.FacebookWebLogin.RateLimitHmacKeyBase64 = Convert.ToBase64String(new byte[31]);
        Assert.True(validator.Validate(null, enabledWithShortKey).Failed);

        var enabledWithMalformedKey = FacebookWebLoginTestHost.CreateReadyOptions();
        enabledWithMalformedKey.FacebookWebLogin.RateLimitHmacKeyBase64 = "not-base64";
        Assert.True(validator.Validate(null, enabledWithMalformedKey).Failed);

        var enabledWithBoundaryKey = FacebookWebLoginTestHost.CreateReadyOptions();
        enabledWithBoundaryKey.FacebookWebLogin.RateLimitHmacKeyBase64 = Convert.ToBase64String(new byte[32]);
        Assert.True(validator.Validate(null, enabledWithBoundaryKey).Succeeded);

        var disabledWithoutKey = FacebookWebLoginTestHost.CreateReadyOptions(webLoginEnabled: false);
        disabledWithoutKey.FacebookWebLogin.RateLimitHmacKeyBase64 = string.Empty;
        Assert.True(validator.Validate(null, disabledWithoutKey).Succeeded);
    }

    [Fact]
    public void RateLimitKey_IsStableDistinctAndDoesNotExposeClientAddress()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        const string firstAddress = "203.0.113.10";
        const string secondAddress = "203.0.113.11";

        var first = RedisFacebookWebLoginRateLimiter.BuildRateLimitKey(key, firstAddress);

        Assert.StartsWith("swiftaid:auth:facebook-web:rate:v2:", first, StringComparison.Ordinal);
        Assert.Equal(first, RedisFacebookWebLoginRateLimiter.BuildRateLimitKey(key, firstAddress));
        Assert.NotEqual(first, RedisFacebookWebLoginRateLimiter.BuildRateLimitKey(key, secondAddress));
        Assert.NotEqual(first, RedisFacebookWebLoginRateLimiter.BuildRateLimitKey(new byte[32], firstAddress));
        Assert.DoesNotContain(firstAddress, first, StringComparison.Ordinal);
        Assert.DoesNotContain(secondAddress, first, StringComparison.Ordinal);
    }

    [DockerRedisFixtureFact]
    [Trait("Category", "DockerRedisFixture")]
    public async Task RedisRateLimiter_UsesAtomicBucketsWithBoundedExpiryAndFailsClosed()
    {
        var connectionString = Environment.GetEnvironmentVariable("SWIFTAID_TEST_REDIS_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var hmacKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var federationOptions = FacebookWebLoginTestHost.CreateReadyOptions();
        federationOptions.FacebookWebLogin.RateLimitHmacKeyBase64 = Convert.ToBase64String(hmacKey);
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString!);
        using var limiter = new RedisFacebookWebLoginRateLimiter(multiplexer, federationOptions);
        var database = multiplexer.GetDatabase();
        var expiryAddress = $"203.0.113.{Random.Shared.Next(1, 255)}";
        var expiryKey = RedisFacebookWebLoginRateLimiter.BuildRateLimitKey(hmacKey, expiryAddress);
        var expiryWindow = TimeSpan.FromSeconds(5);

        Assert.Equal(FacebookWebRateLimitStatus.Allowed,
            await limiter.CheckAsync(expiryAddress, 2, expiryWindow, CancellationToken.None));
        var initialTtl = await database.KeyTimeToLiveAsync(expiryKey);
        Assert.NotNull(initialTtl);
        Assert.InRange(initialTtl!.Value, TimeSpan.FromMilliseconds(1), expiryWindow);
        Assert.Equal(FacebookWebRateLimitStatus.Allowed,
            await limiter.CheckAsync(expiryAddress, 2, expiryWindow, CancellationToken.None));
        Assert.Equal(FacebookWebRateLimitStatus.Limited,
            await limiter.CheckAsync(expiryAddress, 2, expiryWindow, CancellationToken.None));

        var concurrentAddress = $"198.51.100.{Random.Shared.Next(1, 255)}";
        var concurrentResults = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => limiter.CheckAsync(
                concurrentAddress,
                12,
                TimeSpan.FromSeconds(5),
                CancellationToken.None).AsTask()));
        Assert.Equal(12, concurrentResults.Count(result => result == FacebookWebRateLimitStatus.Allowed));
        Assert.Equal(20, concurrentResults.Count(result => result == FacebookWebRateLimitStatus.Limited));

        var expiryDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
        FacebookWebRateLimitStatus afterExpiry;
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            afterExpiry = await limiter.CheckAsync(
                expiryAddress,
                2,
                expiryWindow,
                CancellationToken.None);
        }
        while (afterExpiry == FacebookWebRateLimitStatus.Limited && DateTimeOffset.UtcNow < expiryDeadline);
        Assert.Equal(FacebookWebRateLimitStatus.Allowed, afterExpiry);

        multiplexer.Dispose();
        Assert.Equal(FacebookWebRateLimitStatus.Unavailable,
            await limiter.CheckAsync("192.0.2.44", 2, expiryWindow, CancellationToken.None));
    }

    [Fact]
    public async Task Options_WhenWebLoginIsReady_ReturnsPublicConfigurationWithoutCaching()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync();

        using var response = await host.Client.GetAsync(OptionsPath);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(document.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("facebook-test-app", document.RootElement.GetProperty("appId").GetString());
        Assert.Equal("v24.0", document.RootElement.GetProperty("graphApiVersion").GetString());
        AssertNoStore(response);
    }

    [Fact]
    public async Task Options_WhenCallbackOrWebLoginReadinessIsMissing_OmitsAppConfiguration()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync(callbackVerified: false);

        using var response = await host.Client.GetAsync(OptionsPath);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(document.RootElement.GetProperty("available").GetBoolean());
        AssertPropertyOmittedOrNull(document.RootElement, "appId");
        AssertPropertyOmittedOrNull(document.RootElement, "graphApiVersion");
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_WithValidToken_CreatesOrdinaryNonPersistentSessionWithoutRecentAuthentication()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync();
        using var request = await host.CreateExchangeRequestAsync("ordinary-token", "/cases");

        using var response = await host.Client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/cases", document.RootElement.GetProperty("redirectUrl").GetString());
        AssertNoStore(response);
        Assert.Equal(1, host.Validator.Calls);
        Assert.Equal(1, host.MappingStore.ResolveOrCreateCalls);
        Assert.Equal(FederationSetup.FacebookProviderId, host.MappingStore.LastProvider);
        Assert.Equal(FederationSetup.FacebookIssuer, host.MappingStore.LastIssuer);
        Assert.Equal("facebook-subject-123", host.MappingStore.LastSubject);
        Assert.Equal(0, host.MappingStore.LinkCalls);
        Assert.Equal(0, host.MappingStore.ResolveExistingCalls);
        Assert.Equal(1, host.Authentication.SignInCalls);
        Assert.False(host.Authentication.LastSignInProperties!.IsPersistent);
        Assert.DoesNotContain("swiftaid:reauthenticated-at-utc", host.Authentication.LastSignInProperties.Items.Keys);
        Assert.DoesNotContain("swiftaid:reauthenticated-provider", host.Authentication.LastSignInProperties.Items.Keys);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("swiftaid-endpoint-app=issued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exchange_WhenAlreadySignedIn_RejectsBeforeRateLimitValidationOrMapping()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync();
        using var request = await host.CreateExchangeRequestAsync("ordinary-token", "/cases");
        await host.AuthenticateExchangeRequestAsync(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("already_signed_in", await ReadErrorCodeAsync(response));
        Assert.Equal(0, host.RateLimiter.Calls);
        Assert.Equal(0, host.Validator.Calls);
        Assert.Equal(0, host.MappingStore.ResolveOrCreateCalls);
        Assert.Equal(0, host.Authentication.SignInCalls);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_WithoutAntiforgeryProof_RejectsBeforeRateLimitOrProviderCall()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, ExchangePath)
        {
            Content = JsonContent("ordinary-token", "/cases")
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("antiforgery_invalid", await ReadErrorCodeAsync(response));
        Assert.Equal(0, host.RateLimiter.Calls);
        Assert.Equal(0, host.Validator.Calls);
        Assert.Equal(0, host.MappingStore.ResolveOrCreateCalls);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_WithUnsupportedMediaType_RejectsBeforeProviderCall()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, ExchangePath)
        {
            Content = new StringContent("ordinary-token", Encoding.UTF8, "text/plain")
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_media", await ReadErrorCodeAsync(response));
        Assert.Equal(0, host.Validator.Calls);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_WhenContentLengthExceedsLimit_RejectsBeforeAntiforgeryOrProviderCall()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync(maximumRequestBytes: 256);
        using var request = new HttpRequestMessage(HttpMethod.Post, ExchangePath)
        {
            Content = new ByteArrayContent(new byte[257])
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("oversized", await ReadErrorCodeAsync(response));
        Assert.Equal(0, host.RateLimiter.Calls);
        Assert.Equal(0, host.Validator.Calls);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_WhenUnknownLengthBodyExceedsLimit_RejectsBeforeProviderCall()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync(maximumRequestBytes: 256);
        var oversizedJson = JsonSerializer.Serialize(new { accessToken = new string('x', 400), returnUrl = "/cases" });
        using var request = await host.CreateUnknownLengthExchangeRequestAsync(oversizedJson);
        Assert.Null(request.Content!.Headers.ContentLength);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("oversized", await ReadErrorCodeAsync(response));
        Assert.Equal(1, host.RateLimiter.Calls);
        Assert.Equal(0, host.Validator.Calls);
        Assert.Equal(0, host.MappingStore.ResolveOrCreateCalls);
        AssertNoStore(response);
    }

    [Theory]
    [InlineData("{", "invalid_request")]
    [InlineData("{}", "invalid_request")]
    [InlineData("{\"accessToken\":\"   \"}", "invalid_request")]
    public async Task Exchange_WithMalformedOrMissingToken_RejectsWithoutProviderCall(string body, string expectedCode)
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync();
        using var request = await host.CreateRawExchangeRequestAsync(body);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ReadErrorCodeAsync(response));
        Assert.Equal(1, host.RateLimiter.Calls);
        Assert.Equal(0, host.Validator.Calls);
        Assert.Equal(0, host.MappingStore.ResolveOrCreateCalls);
        AssertNoStore(response);
    }

    [Theory]
    [InlineData(FacebookWebRateLimitStatus.Limited, HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(FacebookWebRateLimitStatus.Unavailable, HttpStatusCode.ServiceUnavailable, "provider_unavailable")]
    public async Task Exchange_WhenSharedRateLimiterDoesNotAllowRequest_ReturnsStableError(
        FacebookWebRateLimitStatus status,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync(rateStatus: status);
        using var request = await host.CreateExchangeRequestAsync("ordinary-token", "/cases");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, await ReadErrorCodeAsync(response));
        Assert.Equal(1, host.RateLimiter.Calls);
        Assert.Equal(0, host.Validator.Calls);
        Assert.Equal(0, host.MappingStore.ResolveOrCreateCalls);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_UsesConnectionAddressAndIgnoresUntrustedForwardedAddress()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync();
        using var request = await host.CreateExchangeRequestAsync("ordinary-token", "/cases");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.77");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("203.0.113.44", host.RateLimiter.LastClientAddress);
    }

    [Theory]
    [InlineData(FacebookTokenValidationStatus.Invalid, HttpStatusCode.Unauthorized, "invalid_provider_token")]
    [InlineData(FacebookTokenValidationStatus.Unavailable, HttpStatusCode.ServiceUnavailable, "provider_unavailable")]
    public async Task Exchange_WhenProviderValidationRejectsToken_DoesNotMapIdentityOrIssueCookie(
        FacebookTokenValidationStatus tokenStatus,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync(tokenStatus: tokenStatus);
        using var request = await host.CreateExchangeRequestAsync("ordinary-token", "/cases");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, await ReadErrorCodeAsync(response));
        Assert.Equal(1, host.Validator.Calls);
        Assert.Equal(0, host.MappingStore.ResolveOrCreateCalls);
        Assert.Equal(0, host.Authentication.SignInCalls);
        Assert.DoesNotContain("Set-Cookie", response.Headers.Select(header => header.Key));
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_WhenProviderReturnsValidStatusWithoutSubject_DoesNotMapOrIssueCookie()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync(tokenSubject: null);
        using var request = await host.CreateExchangeRequestAsync("ordinary-token", "/cases");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("provider_unavailable", await ReadErrorCodeAsync(response));
        Assert.Equal(0, host.MappingStore.ResolveOrCreateCalls);
        Assert.Equal(0, host.Authentication.SignInCalls);
        Assert.DoesNotContain("Set-Cookie", response.Headers.Select(header => header.Key));
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_WhenIdentityResolverRejectsSubject_DoesNotIssueCookie()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync(
            mappingStatus: ExternalIdentityResolutionStatus.Rejected);
        using var request = await host.CreateExchangeRequestAsync("ordinary-token", "/cases");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_provider_token", await ReadErrorCodeAsync(response));
        Assert.Equal(1, host.MappingStore.ResolveOrCreateCalls);
        Assert.Equal(0, host.Authentication.SignInCalls);
        Assert.DoesNotContain("Set-Cookie", response.Headers.Select(header => header.Key));
        AssertNoStore(response);
    }

    [Theory]
    [InlineData("https://attacker.example/steal")]
    [InlineData("//attacker.example/steal")]
    [InlineData("/\\attacker.example")]
    public async Task Exchange_WithUnsafeReturnUrl_UsesLocalDefault(string returnUrl)
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync();
        using var request = await host.CreateExchangeRequestAsync("ordinary-token", returnUrl);

        using var response = await host.Client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/", document.RootElement.GetProperty("redirectUrl").GetString());
        AssertNoStore(response);
    }

    [Fact]
    public async Task Exchange_WhenFeatureIsNotReady_ReturnsUnavailableWithoutProviderCall()
    {
        await using var host = await FacebookWebLoginTestHost.StartAsync(webLoginEnabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, ExchangePath)
        {
            Content = JsonContent("ordinary-token", "/cases")
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("web_login_disabled", await ReadErrorCodeAsync(response));
        Assert.Equal(0, host.RateLimiter.Calls);
        Assert.Equal(0, host.Validator.Calls);
        AssertNoStore(response);
    }

    [DockerFacebookTlsFixtureFact]
    [Trait("Category", "DockerMetaTlsFixture")]
    public async Task ProductionValidator_UsesFixedTlsHostAndDoesNotExposeCredentialsInDiagnostics()
    {
        var hostAddresses = await Dns.GetHostAddressesAsync("graph.facebook.com");
        Assert.NotEmpty(hostAddresses);
        Assert.All(hostAddresses, address => Assert.True(IPAddress.IsLoopback(address),
            "The Docker test runner must resolve graph.facebook.com only to the local TLS fixture."));

        var certificatePath = Environment.GetEnvironmentVariable("SWIFTAID_FACEBOOK_FIXTURE_PFX");
        Assert.False(string.IsNullOrWhiteSpace(certificatePath));
        Assert.True(File.Exists(certificatePath));
        var certificatePassword = Environment.GetEnvironmentVariable("SWIFTAID_FACEBOOK_FIXTURE_PFX_PASSWORD") ?? string.Empty;
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath!, certificatePassword, X509KeyStorageFlags.EphemeralKeySet);

        await using var fixture = await FacebookTlsFixture.StartAsync(certificate);
        using var listener = new FacebookHttpEventListener();
        using var activityListener = FacebookActivityCapture.Start();
        var logCapture = new FacebookLogCapture();

        var options = FacebookWebLoginTestHost.CreateReadyOptions();
        options.FacebookWebLogin.MaximumResponseBytes = 1024;
        options.FacebookWebLogin.OutboundTimeoutSeconds = 1;
        options.Facebook.ClientSecret = "canary-app-secret";
        var validator = new FacebookWebTokenValidator(options, TimeProvider.System);
        await using var host = await FacebookWebLoginTestHost.StartAsync(
            options: options,
            tokenValidator: validator,
            loggerCapture: logCapture);
        Assert.Same(validator, host.ResolveFacebookWebTokenValidator());
        var cases = new (string Token, FacebookTokenValidationStatus ExpectedStatus, string? ExpectedSubject)[]
        {
            ("valid-canary-token", FacebookTokenValidationStatus.Valid, "facebook-subject-123"),
            ("valid-extra-scopes-canary-token", FacebookTokenValidationStatus.Valid, "facebook-subject-123"),
            ("wrong-app-id-canary-token", FacebookTokenValidationStatus.Invalid, null),
            ("wrong-type-canary-token", FacebookTokenValidationStatus.Invalid, null),
            ("invalid-canary-token", FacebookTokenValidationStatus.Invalid, null),
            ("expired-canary-token", FacebookTokenValidationStatus.Invalid, null),
            ("invalid-expiry-canary-token", FacebookTokenValidationStatus.Invalid, null),
            ("missing-scope-canary-token", FacebookTokenValidationStatus.Invalid, null),
            ("missing-subject-canary-token", FacebookTokenValidationStatus.Invalid, null),
            ("non2xx-canary-token", FacebookTokenValidationStatus.Unavailable, null),
            ("malformed-canary-token", FacebookTokenValidationStatus.Unavailable, null),
            ("oversize-canary-token", FacebookTokenValidationStatus.Unavailable, null),
            ("oversize-unknown-canary-token", FacebookTokenValidationStatus.Unavailable, null),
            ("redirect-canary-token", FacebookTokenValidationStatus.Unavailable, null),
            ("timeout-canary-token", FacebookTokenValidationStatus.Unavailable, null)
        };

        foreach (var testCase in cases)
        {
            using var request = await host.CreateExchangeRequestAsync(testCase.Token, "/cases");
            using var response = await host.Client.SendAsync(request);
            if (testCase.ExpectedStatus == FacebookTokenValidationStatus.Valid)
            {
                var directValidationStatus = "not run";
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var directValidation = await validator.ValidateAsync(testCase.Token, CancellationToken.None);
                    directValidationStatus = directValidation.Status.ToString();
                }

                Assert.True(
                    response.StatusCode == HttpStatusCode.OK,
                    $"Error code: {await ReadErrorCodeAsync(response) ?? "none"}; " +
                    $"fixture request count: {fixture.RequestedTokens.Count}; " +
                    $"observed path count: {fixture.ObservedPaths.Count}; " +
                    $"fixture received expected token: {fixture.RequestedTokens.Contains(testCase.Token)}; " +
                    $"mapping calls: {host.MappingStore.ResolveOrCreateCalls}; " +
                    $"subject captured: {host.MappingStore.LastSubject is not null}; " +
                    $"direct validator status: {directValidationStatus}; " +
                    $"HTTP event summaries: {string.Join(" | ", listener.SafeEventSummaries)}.");
                Assert.Equal(cases.TakeWhile(item => item.Token != testCase.Token)
                    .Count(item => item.ExpectedStatus == FacebookTokenValidationStatus.Valid) + 1,
                    host.MappingStore.ResolveOrCreateCalls);
                Assert.Equal(host.MappingStore.ResolveOrCreateCalls, host.Authentication.SignInCalls);
                Assert.Equal(testCase.ExpectedSubject, host.MappingStore.LastSubject);
                Assert.Equal(0, host.MappingStore.LinkCalls);
                Assert.False(host.Authentication.LastSignInProperties!.IsPersistent);
                Assert.DoesNotContain("swiftaid:reauthenticated-at-utc", host.Authentication.LastSignInProperties.Items.Keys);
                Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
                    value.StartsWith("swiftaid-endpoint-app=issued", StringComparison.Ordinal));
            }
            else
            {
                var expectedStatus = testCase.ExpectedStatus == FacebookTokenValidationStatus.Invalid
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.ServiceUnavailable;
                var expectedCode = testCase.ExpectedStatus == FacebookTokenValidationStatus.Invalid
                    ? "invalid_provider_token"
                    : "provider_unavailable";
                Assert.Equal(expectedStatus, response.StatusCode);
                var errorBody = await response.Content.ReadAsStringAsync();
                Assert.Equal(expectedCode, ReadErrorCode(errorBody));
                Assert.DoesNotContain("provider detail must not escape", errorBody, StringComparison.Ordinal);
                Assert.DoesNotContain("Set-Cookie", response.Headers.Select(header => header.Key));
            }
            AssertNoStore(response);
        }

        Assert.Contains(fixture.RequestedTokens, token => token == "valid-canary-token");
        Assert.DoesNotContain("redirect-followed", fixture.RequestedTokens);
        Assert.DoesNotContain("timeout-completed", fixture.RequestedTokens);
        Assert.Equal(cases.Length, fixture.RequestedTokens.Count);

        var diagnostics = string.Join("\n", listener.Events.Concat(activityListener.Events)
            .Concat(logCapture.Messages)
            .Concat(host.LoggerCapture.Messages));
        foreach (var testCase in cases)
        {
            Assert.DoesNotContain(testCase.Token, diagnostics, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("provider detail must not escape", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("canary-app-secret", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("facebook-test-app|canary-app-secret", diagnostics, StringComparison.Ordinal);
        Assert.Contains(fixture.ObservedPaths, path => path.StartsWith("/v24.0/debug_token?", StringComparison.Ordinal));
        Assert.Contains(fixture.ObservedPaths, path => path.Contains("access_token=facebook-test-app%7Ccanary-app-secret", StringComparison.Ordinal));
    }

    private static StringContent JsonContent(string accessToken, string? returnUrl) =>
        new(JsonSerializer.Serialize(new { accessToken, returnUrl }), Encoding.UTF8, "application/json");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        return ReadErrorCode(await response.Content.ReadAsStringAsync());
    }

    private static string? ReadErrorCode(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", response.Headers.TryGetValues("Pragma", out var pragma)
            ? string.Join(",", pragma)
            : string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPropertyOmittedOrNull(JsonElement element, string propertyName)
    {
        Assert.True(!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null);
    }
}

/// <summary>
/// This opt-in fact can execute only in the isolated Docker runner that maps graph.facebook.com to the
/// local test fixture and trusts its CA inside that disposable container. It never accepts a TLS bypass.
/// </summary>
internal sealed class DockerFacebookTlsFixtureFactAttribute : FactAttribute
{
    public DockerFacebookTlsFixtureFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SWIFTAID_FACEBOOK_TLS_FIXTURE"), "1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_FACEBOOK_FIXTURE_PFX")))
        {
            Skip = "Requires the isolated Docker graph.facebook.com TLS fixture and container-scoped trusted CA.";
        }
    }
}

internal sealed class DockerRedisFixtureFactAttribute : FactAttribute
{
    public DockerRedisFixtureFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_REDIS_CONNECTION")))
        {
            Skip = "Requires the disposable Redis service on the task-owned internal Docker network.";
        }
    }
}

internal sealed class FacebookWebLoginTestHost : IAsyncDisposable
{
    private const string CsrfHeader = FacebookWebLoginExchangeTests.CsrfHeader;
    private readonly WebApplication _application;
    private readonly IAntiforgery _antiforgery;
    private readonly FacebookLogCapture _loggerCapture;
    private string? _antiforgeryCookie;

    private FacebookWebLoginTestHost(
        WebApplication application,
        HttpClient client,
        FacebookTokenValidatorProbe validator,
        FacebookRateLimiterProbe rateLimiter,
        FacebookMappingStoreProbe mappingStore,
        FacebookAuthenticationProbe authentication,
        FacebookLogCapture loggerCapture)
    {
        _application = application;
        Client = client;
        Validator = validator;
        RateLimiter = rateLimiter;
        MappingStore = mappingStore;
        Authentication = authentication;
        _loggerCapture = loggerCapture;
        _antiforgery = application.Services.GetRequiredService<IAntiforgery>();
    }

    public HttpClient Client { get; }

    public FacebookTokenValidatorProbe Validator { get; }

    public FacebookRateLimiterProbe RateLimiter { get; }

    public FacebookMappingStoreProbe MappingStore { get; }

    public FacebookAuthenticationProbe Authentication { get; }

    public IFacebookWebTokenValidator ResolveFacebookWebTokenValidator() =>
        _application.Services.GetRequiredService<IFacebookWebTokenValidator>();

    public FacebookLogCapture LoggerCapture => _loggerCapture;

    public static FederationOptions CreateReadyOptions(
        bool callbackVerified = true,
        bool webLoginEnabled = true,
        int maximumRequestBytes = 4096) => new()
    {
        Enabled = true,
        Facebook = new SocialProviderOptions
        {
            Enabled = true,
            CallbackVerified = callbackVerified,
            ClientId = "facebook-test-app",
            ClientSecret = "canary-app-secret"
        },
        FacebookWebLogin = new FacebookWebLoginSettings
        {
            Enabled = webLoginEnabled,
                RateLimitHmacKeyBase64 = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            GraphApiVersion = "v24.0",
            MaximumRequestBytes = maximumRequestBytes,
            MaximumResponseBytes = 16384,
            OutboundTimeoutSeconds = 5,
            MaximumConcurrentValidations = 4,
            MaximumAttempts = 5,
            RateLimitWindowSeconds = 60
        }
    };

    public static async Task<FacebookWebLoginTestHost> StartAsync(
        bool callbackVerified = true,
        bool webLoginEnabled = true,
        int maximumRequestBytes = 4096,
        FacebookWebRateLimitStatus rateStatus = FacebookWebRateLimitStatus.Allowed,
        FacebookTokenValidationStatus tokenStatus = FacebookTokenValidationStatus.Valid,
        string? tokenSubject = "facebook-subject-123",
        ExternalIdentityResolutionStatus mappingStatus = ExternalIdentityResolutionStatus.Resolved,
        FederationOptions? options = null,
        IFacebookWebTokenValidator? tokenValidator = null,
        FacebookLogCapture? loggerCapture = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        loggerCapture ??= new FacebookLogCapture();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(loggerCapture);
        builder.Services.AddDbContext<SwiftAidDbContext>(dbOptions =>
            dbOptions.UseNpgsql("Host=127.0.0.1;Database=swift_aid_model_only;Username=model_only")
                .UseOpenIddict());
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeader;
            options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        });

        var federationOptions = options ?? CreateReadyOptions(callbackVerified, webLoginEnabled, maximumRequestBytes);
        var validator = new FacebookTokenValidatorProbe(tokenStatus, tokenSubject);
        var rateLimiter = new FacebookRateLimiterProbe(rateStatus);
        var mappingStore = new FacebookMappingStoreProbe(mappingStatus);
        var authentication = new FacebookAuthenticationProbe();

        builder.Services.AddSingleton(federationOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(validator);
        if (tokenValidator is null)
        {
            builder.Services.AddSingleton<IFacebookWebTokenValidator>(services =>
                services.GetRequiredService<FacebookTokenValidatorProbe>());
        }
        else
        {
            builder.Services.AddSingleton(tokenValidator);
        }
        builder.Services.AddSingleton(rateLimiter);
        builder.Services.AddSingleton<IFacebookWebLoginRateLimiter>(services =>
            services.GetRequiredService<FacebookRateLimiterProbe>());
        builder.Services.AddSingleton(mappingStore);
        builder.Services.AddSingleton<IExternalIdentityMappingStore>(services =>
            services.GetRequiredService<FacebookMappingStoreProbe>());
        builder.Services.AddSingleton<IExternalIdentityResolver>(services => new ExternalIdentityResolver(
            federationOptions,
            new OrganizationSsoRegistry(federationOptions),
            services.GetRequiredService<IExternalIdentityMappingStore>()));
        builder.Services.AddSingleton(authentication);
        builder.Services.AddIdentityCore<SwiftAidUser>()
            .AddSignInManager();
        builder.Services.AddSingleton<IUserStore<SwiftAidUser>, FacebookUserStore>();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, FacebookAuthenticationHandler>(
                IdentityConstants.ApplicationScheme,
                _ => { });

        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.44");
            await next().ConfigureAwait(false);
        });
        application.UseAuthentication();
        application.MapSwiftAidIdentityEndpoints(federationOptions, new IdentityServerOptions());
        await application.StartAsync().ConfigureAwait(false);

        return new FacebookWebLoginTestHost(
            application,
            application.GetTestClient(),
            validator,
            rateLimiter,
            mappingStore,
            authentication,
            loggerCapture);
    }

    public async Task<HttpRequestMessage> CreateExchangeRequestAsync(string accessToken, string? returnUrl)
    {
        var body = JsonSerializer.Serialize(new { accessToken, returnUrl });
        return await CreateRawExchangeRequestAsync(body).ConfigureAwait(false);
    }

    public async Task<HttpRequestMessage> CreateRawExchangeRequestAsync(string body)
    {
        var antiforgeryToken = await GetAntiforgeryAsync().ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/facebook/web/exchange")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Cookie", _antiforgeryCookie);
        request.Headers.TryAddWithoutValidation(CsrfHeader, antiforgeryToken);
        return request;
    }

    public async Task<HttpRequestMessage> CreateUnknownLengthExchangeRequestAsync(string body)
    {
        var antiforgeryToken = await GetAntiforgeryAsync().ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/facebook/web/exchange")
        {
            Content = new UnknownLengthJsonContent(body)
        };
        request.Headers.TryAddWithoutValidation("Cookie", _antiforgeryCookie);
        request.Headers.TryAddWithoutValidation(CsrfHeader, antiforgeryToken);
        return request;
    }

    public async Task AuthenticateExchangeRequestAsync(HttpRequestMessage request)
    {
        var antiforgeryToken = await GetAntiforgeryAsync(authenticated: true).ConfigureAwait(false);
        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{_antiforgeryCookie}; swiftaid-endpoint-app=authenticated");
        request.Headers.Remove(CsrfHeader);
        request.Headers.TryAddWithoutValidation(CsrfHeader, antiforgeryToken);
    }

    private async Task<string> GetAntiforgeryAsync(bool authenticated = false)
    {
        var context = new DefaultHttpContext { RequestServices = _application.Services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "facebook-test-actor")],
            IdentityConstants.ApplicationScheme));
        }

        var tokens = _antiforgery.GetAndStoreTokens(context);
        _antiforgeryCookie = context.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        return tokens.RequestToken ?? throw new InvalidOperationException("Antiforgery token was not created.");
    }

    public ValueTask DisposeAsync() => new(_application.DisposeAsync().AsTask());
}

internal sealed class UnknownLengthJsonContent : HttpContent
{
    private readonly byte[] _bytes;

    public UnknownLengthJsonContent(string body)
    {
        _bytes = Encoding.UTF8.GetBytes(body);
        Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(_bytes, 0, _bytes.Length);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_bytes);
        }

        base.Dispose(disposing);
    }
}

internal sealed class FacebookTokenValidatorProbe(
    FacebookTokenValidationStatus status,
    string? subject) : IFacebookWebTokenValidator
{
    public int Calls { get; private set; }

    public string? LastToken { get; private set; }

    public ValueTask<FacebookTokenValidationResult> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastToken = accessToken;
        return ValueTask.FromResult(new FacebookTokenValidationResult(status, subject));
    }
}

internal sealed class FacebookRateLimiterProbe(FacebookWebRateLimitStatus status) : IFacebookWebLoginRateLimiter
{
    public int Calls { get; private set; }

    public string? LastClientAddress { get; private set; }

    public ValueTask<FacebookWebRateLimitStatus> CheckAsync(
        string clientAddress,
        int maximumAttempts,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastClientAddress = clientAddress;
        return ValueTask.FromResult(status);
    }
}

internal sealed class FacebookMappingStoreProbe(ExternalIdentityResolutionStatus status) : IExternalIdentityMappingStore
{
    public int ResolveOrCreateCalls { get; private set; }

    public int ResolveExistingCalls { get; private set; }

    public int LinkCalls { get; private set; }

    public string? LastProvider { get; private set; }

    public string? LastIssuer { get; private set; }

    public string? LastSubject { get; private set; }

    public ValueTask<ExternalIdentityResolution> ResolveOrCreateOrdinaryActorAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        ResolveOrCreateCalls++;
        LastProvider = provider;
        LastIssuer = issuer;
        LastSubject = subject;
        return ValueTask.FromResult(status == ExternalIdentityResolutionStatus.Resolved
            ? new ExternalIdentityResolution(status, "facebook-test-actor")
            : new ExternalIdentityResolution(status));
    }

    public ValueTask<ExternalIdentityResolution> ResolveExistingAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        ResolveExistingCalls++;
        return ValueTask.FromResult(new ExternalIdentityResolution(ExternalIdentityResolutionStatus.Rejected));
    }

    public ValueTask<ExternalIdentityLinkStatus> LinkAsync(
        string actorId,
        string provider,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        LinkCalls++;
        return ValueTask.FromResult(ExternalIdentityLinkStatus.Rejected);
    }

    public ValueTask<ExternalIdentityUnlinkStatus> UnlinkProviderAsync(
        string actorId,
        string provider,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ExternalIdentityUnlinkStatus.Rejected);
}

internal sealed class FacebookAuthenticationProbe
{
    public int SignInCalls { get; set; }

    public AuthenticationProperties? LastSignInProperties { get; set; }
}

internal sealed class FacebookAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    FacebookAuthenticationProbe probe)
    : SignInAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue("swiftaid-endpoint-app", out var value) || value != "authenticated")
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "facebook-test-actor")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), new AuthenticationProperties(), Scheme.Name)));
    }

    protected override Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties)
    {
        probe.SignInCalls++;
        probe.LastSignInProperties = properties;
        Response.Headers.Append("Set-Cookie", "swiftaid-endpoint-app=issued; Path=/; HttpOnly; SameSite=Lax");
        return Task.CompletedTask;
    }

    protected override Task HandleSignOutAsync(AuthenticationProperties? properties) => Task.CompletedTask;
}

internal sealed class FacebookUserStore : IUserStore<SwiftAidUser>
{
    public Task<IdentityResult> CreateAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Success);

    public Task<IdentityResult> UpdateAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Success);

    public Task<IdentityResult> DeleteAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Success);

    public Task<SwiftAidUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult<SwiftAidUser?>(userId == "facebook-test-actor"
            ? new SwiftAidUser { Id = userId, UserName = userId }
            : null);

    public Task<SwiftAidUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        Task.FromResult<SwiftAidUser?>(null);

    public Task<string> GetUserIdAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
        Task.FromResult(user.UserName);

    public Task SetUserNameAsync(SwiftAidUser user, string? userName, CancellationToken cancellationToken)
    {
        user.UserName = userName;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(SwiftAidUser user, CancellationToken cancellationToken) =>
        Task.FromResult(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(SwiftAidUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

internal sealed class FacebookTlsFixture : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentQueue<string> _requestedTokens = new();
    private readonly ConcurrentQueue<string> _observedPaths = new();

    private FacebookTlsFixture(WebApplication application)
    {
        _application = application;
    }

    public IReadOnlyCollection<string> RequestedTokens => _requestedTokens.ToArray();

    public IReadOnlyCollection<string> ObservedPaths => _observedPaths.ToArray();

    public static async Task<FacebookTlsFixture> StartAsync(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration["AllowedHosts"] = "graph.facebook.com";
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(443, listen => listen.UseHttps(certificate)));
        var application = builder.Build();
        var fixture = new FacebookTlsFixture(application);
        application.MapGet("/{version}/debug_token", async context =>
        {
            var path = string.Concat(
                context.Request.Path.ToUriComponent(),
                context.Request.QueryString.ToUriComponent());
            fixture._observedPaths.Enqueue(path);
            var token = context.Request.Query["input_token"].ToString();
            fixture._requestedTokens.Enqueue(token);
            switch (token)
            {
                case "valid-canary-token":
                case "valid-extra-scopes-canary-token":
                case "wrong-app-id-canary-token":
                case "wrong-type-canary-token":
                case "invalid-canary-token":
                case "expired-canary-token":
                case "invalid-expiry-canary-token":
                case "missing-scope-canary-token":
                case "missing-subject-canary-token":
                    context.Response.ContentType = "application/json";
                    var tokenData = new Dictionary<string, object?>
                    {
                        ["app_id"] = token == "wrong-app-id-canary-token" ? "another-app" : "facebook-test-app",
                        ["type"] = token == "wrong-type-canary-token" ? "APP" : "USER",
                        ["is_valid"] = token != "invalid-canary-token",
                        ["expires_at"] = token switch
                        {
                            "expired-canary-token" => DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
                            "invalid-expiry-canary-token" => long.MaxValue,
                            _ => DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
                        },
                        ["scopes"] = token switch
                        {
                            "missing-scope-canary-token" => Array.Empty<string>(),
                            "valid-extra-scopes-canary-token" => new[] { "public_profile", "email", "user_friends" },
                            _ => new[] { "public_profile" }
                        }
                    };
                    if (token != "missing-subject-canary-token")
                    {
                        tokenData["user_id"] = "facebook-subject-123";
                    }
                    await context.Response.WriteAsJsonAsync(new { data = tokenData }).ConfigureAwait(false);
                    break;
                case "non2xx-canary-token":
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await context.Response.WriteAsync("provider detail must not escape").ConfigureAwait(false);
                    break;
                case "malformed-canary-token":
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{malformed").ConfigureAwait(false);
                    break;
                case "oversize-canary-token":
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength = 2048;
                    await context.Response.WriteAsync(new string('x', 2048)).ConfigureAwait(false);
                    break;
                case "oversize-unknown-canary-token":
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(new string('x', 2048)).ConfigureAwait(false);
                    break;
                case "redirect-canary-token":
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers.Location =
                        "https://graph.facebook.com/v24.0/debug_token?input_token=redirect-followed";
                    break;
                case "timeout-canary-token":
                    await Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted).ConfigureAwait(false);
                    fixture._requestedTokens.Enqueue("timeout-completed");
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
            }
        });
        await application.StartAsync().ConfigureAwait(false);
        return fixture;
    }

    public async ValueTask DisposeAsync() => await _application.DisposeAsync().ConfigureAwait(false);
}

internal sealed class FacebookHttpEventListener : EventListener
{
    private readonly ConcurrentQueue<string> _events = new();
    private readonly ConcurrentQueue<string> _safeEventSummaries = new();

    public IReadOnlyCollection<string> Events => _events.ToArray();

    public IReadOnlyCollection<string> SafeEventSummaries => _safeEventSummaries.ToArray();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Net.Http")
        {
            EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        _events.Enqueue(string.Join(" ", eventData.Payload?.Select(value => value?.ToString()) ?? []));
        var statusCode = eventData.PayloadNames is null || eventData.Payload is null
            ? null
            : eventData.PayloadNames
                .Select((name, index) => (name, index))
                .Where(item => string.Equals(item.name, "statusCode", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index < eventData.Payload.Count ? eventData.Payload[item.index]?.ToString() : null)
                .FirstOrDefault(value => value is not null);
        _safeEventSummaries.Enqueue($"{eventData.EventName ?? "event"} statusCode={statusCode ?? "none"}");
    }
}

internal sealed class FacebookActivityCapture : IDisposable
{
    private readonly ConcurrentQueue<string> _events = new();
    private readonly ActivityListener _listener;

    private FacebookActivityCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => _events.Enqueue(string.Join(" ",
                new[] { activity.DisplayName }
                    .Concat(activity.Tags.Select(tag => $"{tag.Key}={tag.Value}"))
                    .Concat(activity.Baggage.Select(item => $"{item.Key}={item.Value}"))))
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyCollection<string> Events => _events.ToArray();

    public static FacebookActivityCapture Start() => new();

    public void Dispose() => _listener.Dispose();
}

internal sealed class FacebookLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(_messages, categoryName);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(ConcurrentQueue<string> messages, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Enqueue($"{categoryName} {formatter(state, exception)} {exception?.Message}");
    }
}
