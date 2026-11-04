using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Surveillance;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class SurveillanceMapDetailsEndpointTests
{
    [Fact]
    public async Task RequiresAuthenticationAndUsesTheValidatedPrincipal()
    {
        var service = new FakeService();
        await using var app = await StartAsync(service);
        using var http = app.GetTestClient();
        using var anonymous = await http.GetAsync("/api/v1/surveillance/map-details?country=SZ&period=days30");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(service.Principal);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var response = await http.GetAsync("/api/v1/surveillance/map-details?country=sz&period=days90&actorId=other");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("actor-1", service.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("SZ", service.Country);
        Assert.Equal(MapAggregatePeriod.Days90, service.Period);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<SurveillanceMapDetails>());
    }

    [Theory]
    [InlineData("SZZ", "days30")]
    [InlineData("SZ", "unknown")]
    public async Task RejectsInvalidFiltersBeforeCallingService(string country, string period)
    {
        var service = new FakeService();
        await using var app = await StartAsync(service);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var response = await http.GetAsync($"/api/v1/surveillance/map-details?country={country}&period={period}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(service.Principal);
    }

    [Fact]
    public async Task MobileClientReadsEndpointProjection()
    {
        var service = new FakeService();
        await using var app = await StartAsync(service);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var result = await new SurveillanceMapDetailsHttpClient(http).GetAsync("SZ", MapAggregatePeriod.Days30);
        Assert.True(result.Succeeded);
        Assert.Equal("Town", Assert.Single(result.Value!.Points).Label);
        Assert.Equal("actor-1", service.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task WorkspaceSelectionIsPassedToTheAuthorizedProjection()
    {
        var service = new FakeService();
        await using var app = await StartAsync(service);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var response = await http.GetAsync("/api/v1/surveillance/map-details?country=SZ&period=days30&workspace=workspace-b");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("workspace-b", service.Workspace);
    }

    private static async Task<WebApplication> StartAsync(FakeService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ISurveillanceMapDetailsRequest>(service);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSurveillanceMapDetailsEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class FakeService : ISurveillanceMapDetailsRequest
    {
        public ClaimsPrincipal? Principal { get; private set; }
        public string? Country { get; private set; }
        public MapAggregatePeriod Period { get; private set; }
        public string? Workspace { get; private set; }
        public Task<UiCapabilityResult<SurveillanceMapDetails>> GetAsync(ClaimsPrincipal principal, string country,
            MapAggregatePeriod period, string? workspace, CancellationToken cancellationToken)
        {
            Principal = principal;
            Country = country;
            Period = period;
            Workspace = workspace;
            return Task.FromResult(new UiCapabilityResult<SurveillanceMapDetails>(UiCapabilityResultCode.Success,
                new([new("town", "Town", -26, 31, 5, 0)], [], false, 0),
                new("surveillance-map-details", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true)));
        }
    }
}
