using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace SwiftAid.Integration.Tests;

public sealed class DisabledIdentityChallengeTests
{
    [Fact]
    public async Task ClassificationEndpointUsesConfiguredStaffPolicyWithoutLocalBearerHandler()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Enabled"] = "false",
            ["Federation:Enabled"] = "true"
        });
        GatewayAuthenticationRegistration.Configure(builder.Services, builder.Configuration);
        builder.Services.AddAuthentication().AddCookie(IdentityConstants.ApplicationScheme);
        builder.Services.AddSingleton<SwiftAid.Application.Classification.ICaseClassificationQuery, UnusedClassificationQuery>();
        await using var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        SwiftAid.Gateway.Endpoints.CaseClassificationEndpoints.MapCaseClassificationEndpoints(application);
        await application.StartAsync();

        using var response = await application.GetTestClient().GetAsync($"/api/v1/cases/{Guid.NewGuid()}/classification");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    private sealed class UnusedClassificationQuery : SwiftAid.Application.Classification.ICaseClassificationQuery
    {
        public Task<IReadOnlyList<SwiftAid.Application.Classification.CaseClassificationEvidence>?> GetAsync(
            SwiftAid.Application.Authorization.ActorContext actor, Guid caseId, CancellationToken ct) =>
            throw new InvalidOperationException("An anonymous caller must not reach the query.");
    }

    [Fact]
    public async Task ProtectedPageRedirectsToSignInWithItsLocalReturnUrlWhenIdentityIsDisabled()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.GetTestClient().GetAsync("/dashboard?tab=assigned");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = new Uri(new Uri("http://localhost"), Assert.IsType<Uri>(response.Headers.Location));
        Assert.Equal("/sign-in", location.AbsolutePath);
        Assert.Equal(
            "/dashboard?tab=assigned",
            QueryHelpers.ParseQuery(location.Query)["returnUrl"].ToString());
    }

    [Fact]
    public async Task ProtectedApiReturnsUnauthorizedInsteadOfRedirectingWhenIdentityIsDisabled()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.GetTestClient().GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task DeniedJsonApiPostKeepsItsUnauthorizedStatusInsteadOfRenderingNotFoundPage()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.GetTestClient().PostAsJsonAsync(
            "/api/v1/ellai/sessions",
            new { mode = 0, clientRequestId = "anonymous-probe" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("incorrect Content-type", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FederatedCookieAuthenticationDoesNotRequireDisabledLocalBearerValidation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Enabled"] = "false",
            ["Federation:Enabled"] = "true"
        });

        Assert.True(GatewayAuthenticationRegistration.Configure(builder.Services, builder.Configuration));
        builder.Services.AddAuthentication()
            .AddCookie(IdentityConstants.ApplicationScheme);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapPost("/api/v1/ellai/sessions", () => Results.Ok())
            .RequireAuthorization("SwiftAidStaffApi");
        await application.StartAsync();

        await using (application)
        {
            using var response = await application.GetTestClient().PostAsync(
                "/api/v1/ellai/sessions", content: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Enabled"] = "false",
            ["Federation:Enabled"] = "false"
        });

        var identityEnabled = GatewayAuthenticationRegistration.Configure(
            builder.Services,
            builder.Configuration);
        Assert.False(identityEnabled);

        var application = builder.Build();
        application.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
            branch => branch.UseStatusCodePagesWithReExecute("/not-found"));
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet("/dashboard", () => Results.Ok("private dashboard"))
            .RequireAuthorization();
        application.MapGet("/api/cases", () => Results.Ok("private cases"))
            .RequireAuthorization("SwiftAidStaffApi");
        application.MapPost("/api/v1/ellai/sessions", () => Results.Ok())
            .RequireAuthorization("SwiftAidStaffApi");
        application.MapGet("/not-found", () => Results.NotFound());
        await application.StartAsync();

        return application;
    }
}
