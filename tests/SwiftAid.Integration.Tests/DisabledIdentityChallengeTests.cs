using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace SwiftAid.Integration.Tests;

public sealed class DisabledIdentityChallengeTests
{
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
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet("/dashboard", () => Results.Ok("private dashboard"))
            .RequireAuthorization();
        application.MapGet("/api/cases", () => Results.Ok("private cases"))
            .RequireAuthorization("SwiftAidStaffApi");
        await application.StartAsync();

        return application;
    }
}
