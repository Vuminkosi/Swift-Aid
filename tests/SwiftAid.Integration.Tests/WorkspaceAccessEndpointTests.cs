using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class WorkspaceAccessEndpointTests
{
    [Fact]
    public async Task RequiresAuthenticationAndUsesPrincipalForSelectedWorkspace()
    {
        var service = new FakeWorkspaceRequest();
        await using var app = await StartAsync(service);
        using var http = app.GetTestClient();
        using var anonymous = await http.GetAsync("/api/v1/me/workspaces?workspace=allowed");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(service.Principal);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var allowed = await http.GetAsync("/api/v1/me/workspaces?workspace=allowed&actorId=other");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("actor-1", service.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(allowed.Headers.CacheControl?.NoStore);
        using var denied = await http.GetAsync("/api/v1/me/workspaces?workspace=other");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task MobileWorkspaceClientUsesTheAuthenticatedEndpoint()
    {
        var service = new FakeWorkspaceRequest();
        await using var app = await StartAsync(service);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var client = new WorkspaceAccessHttpClient(http);
        Assert.Equal("allowed", (await client.GetStateAsync()).Value?.SelectedWorkspaceId);
        Assert.Equal("allowed", (await client.SelectWorkspaceAsync("allowed")).Value?.SelectedWorkspaceId);
        Assert.Equal(UiCapabilityResultCode.Denied, (await client.SelectWorkspaceAsync("other")).Code);
    }

    private static async Task<WebApplication> StartAsync(FakeWorkspaceRequest service)
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
        builder.Services.AddSingleton<IWorkspaceAccessRequest>(service);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorkspaceAccessEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class FakeWorkspaceRequest : IWorkspaceAccessRequest
    {
        public ClaimsPrincipal? Principal { get; private set; }
        public Task<UiCapabilityResult<WorkspaceAccessState>> GetAsync(ClaimsPrincipal principal, string? workspace, CancellationToken ct)
        {
            Principal = principal;
            var code = workspace is null or "allowed" ? UiCapabilityResultCode.Success : UiCapabilityResultCode.Denied;
            var state = code == UiCapabilityResultCode.Success ? new WorkspaceAccessState([new("allowed", "Allowed")], "allowed") : null;
            return Task.FromResult(new UiCapabilityResult<WorkspaceAccessState>(code, state,
                new("workspace-display-context", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success)));
        }
    }
}
