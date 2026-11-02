using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Surveillance;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

/// <summary>In-memory endpoint tests for the staff surveillance area aggregates.</summary>
public sealed class MapAggregateEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset AsOf = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MappedRoute_RequiresAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IMapAggregateQuery>(new StubAggregateQuery());
        builder.Services.AddAuthorization(options => options.AddPolicy("SwiftAidStaffApi", policy =>
            policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme,
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
        using var app = builder.Build();
        app.MapMapAggregateEndpoints();

        var route = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/api/v1/surveillance/area-aggregates");

        var authorization = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal("SwiftAidStaffApi", authorization.Policy);
    }

    [Fact]
    public async Task AllowedAggregate_ReturnsOkWithAreas()
    {
        await using var host = await TestAggregateHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/surveillance/area-aggregates");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<SurveillanceAggregateResult>(body, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(SurveillanceAggregateAccess.Allowed, result!.Access);
        var area = Assert.Single(result.Areas);
        Assert.Equal("SZ", area.AreaCode);
        Assert.True(area.IsValid);
        using var document = JsonDocument.Parse(body);
        var serializedArea = Assert.Single(document.RootElement.GetProperty("areas").EnumerateArray());
        Assert.False(serializedArea.TryGetProperty("caseId", out _));
        Assert.False(serializedArea.TryGetProperty("latitude", out _));
        Assert.False(serializedArea.TryGetProperty("longitude", out _));
        Assert.False(serializedArea.TryGetProperty("observationId", out _));
        Assert.Equal(TimeSpan.FromDays(30), host.Service.LastWindow);
        Assert.Null(host.Service.LastRegionCode);
    }

    [Fact]
    public async Task PeriodAndRegion_AreForwardedToQuery()
    {
        await using var host = await TestAggregateHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/surveillance/area-aggregates?period=days90&regionCode=SZ-HH");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromDays(90), host.Service.LastWindow);
        Assert.Equal("SZ-HH", host.Service.LastRegionCode);
    }

    [Theory]
    [InlineData("/api/v1/surveillance/area-aggregates?period=weekly")]
    [InlineData("/api/v1/surveillance/area-aggregates?regionCode=Z")]
    [InlineData("/api/v1/surveillance/area-aggregates?regionCode=SZ!")]
    public async Task InvalidFilters_AreRejectedWithoutCallingQuery(string path)
    {
        await using var host = await TestAggregateHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task UnavailableAggregate_ReturnsServiceUnavailable()
    {
        await using var host = await TestAggregateHost.StartAsync(access: SurveillanceAggregateAccess.Unavailable);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/surveillance/area-aggregates");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequest_IsRejected()
    {
        await using var host = await TestAggregateHost.StartAsync();
        using var response = await host.Client.GetAsync("/api/v1/surveillance/area-aggregates");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task DisabledPipeline_GetsNotFound()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("SwiftAidStaffApi", policy =>
            policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme,
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
        // No IMapAggregateQuery: the case command pipeline is disabled here.
        using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMapAggregateEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/surveillance/area-aggregates");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.StopAsync();
    }

    private sealed class StubAggregateQuery(
        SurveillanceAggregateAccess access = SurveillanceAggregateAccess.Allowed) : IMapAggregateQuery
    {
        public int Calls { get; private set; }
        public string? LastRegionCode { get; private set; }
        public TimeSpan LastWindow { get; private set; }

        public Task<SurveillanceAggregateResult> GetAsync(
            ActorContext actor, string? regionCode, TimeSpan window, CancellationToken ct)
        {
            Calls++;
            LastRegionCode = regionCode;
            LastWindow = window;
            return Task.FromResult(new SurveillanceAggregateResult(
                access,
                access == SurveillanceAggregateAccess.Allowed
                    ? [new SurveillanceAggregateArea(
                        "SZ",
                        "Eswatini",
                        SurveillanceAggregatePrecision.Country,
                        5,
                        5,
                        10,
                        false,
                        null,
                        AsOf)]
                    : [],
                AsOf,
                false));
        }
    }

    private sealed class TestAggregateHost(WebApplication application, HttpClient client, StubAggregateQuery service)
        : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public StubAggregateQuery Service { get; } = service;
        private readonly WebApplication _application = application;

        public static async Task<TestAggregateHost> StartAsync(
            SurveillanceAggregateAccess access = SurveillanceAggregateAccess.Allowed)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    IdentityConstants.ApplicationScheme, _ => { })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("SwiftAidStaffApi", policy =>
            policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme,
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).RequireAuthenticatedUser()));

            var service = new StubAggregateQuery(access);
            builder.Services.AddSingleton<IMapAggregateQuery>(service);

            var application = builder.Build();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapMapAggregateEndpoints();
            await application.StartAsync().ConfigureAwait(false);

            return new TestAggregateHost(application, application.GetTestClient(), service);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
