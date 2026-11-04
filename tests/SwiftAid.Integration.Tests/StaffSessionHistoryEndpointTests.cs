using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Sessions;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class StaffSessionHistoryEndpointTests
{
    [Fact]
    public async Task ClientAndEndpointRequireAuthenticationAndUseThePrincipal()
    {
        var reader = new Reader();
        await using var app = await StartAsync(reader);
        using var http = app.GetTestClient();
        var id = Guid.NewGuid();
        using var anonymous = await http.GetAsync($"/api/v1/sessions/{id:D}/history");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(reader.Actor);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var result = await new StaffSessionHistoryHttpClient(http).ReadAsync(id, null);
        Assert.True(result.Succeeded);
        Assert.Equal("actor-1", reader.Actor);
        Assert.Equal(id, result.Value!.SessionId);
        using var response = await http.GetAsync($"/api/v1/sessions/{id:D}/history");
        Assert.True(response.Headers.CacheControl?.NoStore);
        reader.Deny = true;
        Assert.Equal(UiCapabilityResultCode.Denied, (await new StaffSessionHistoryHttpClient(http).ReadAsync(id, null)).Code);
    }

    [Fact]
    public async Task StaleAccountCannotReadSessionHistory()
    {
        var reader = new Reader();
        await using var app = await StartAsync(reader, new SyntheticWorkspaceAccessRequest { Current = false });
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        var result = await new StaffSessionHistoryHttpClient(http).ReadAsync(Guid.NewGuid(), null);
        Assert.Equal(UiCapabilityResultCode.Denied, result.Code);
        Assert.Null(reader.Actor);
    }

    private static async Task<WebApplication> StartAsync(Reader reader, SyntheticWorkspaceAccessRequest? access = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ICommunitySessionHistoryReader>(reader);
        builder.Services.AddSingleton<IWorkspaceAccessRequest>(access ?? new SyntheticWorkspaceAccessRequest());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCommunitySessionEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class Reader : ICommunitySessionHistoryReader
    {
        public string? Actor { get; private set; }
        public bool Deny { get; set; }
        public Task<SessionHistoryPage?> ReadAsync(Guid sessionId, string actorId, long? beforeVersion, int limit, CancellationToken cancellationToken)
        {
            Actor = actorId;
            return Task.FromResult<SessionHistoryPage?>(Deny ? null : new(sessionId,
                [new(1, actorId, "synthetic reason", DateTimeOffset.UtcNow, null, "Draft", null, null)], null));
        }
    }
}
