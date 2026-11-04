using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class CaseMonitoringPageEndpointTests
{
    [Fact]
    public async Task EndpointRequiresAuthenticationAndPreservesVersionAndDenial()
    {
        var fake = new FakeQuery();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ICaseMonitoringPageQuery>(fake);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCaseMonitoringPageEndpoints();
        await app.StartAsync();
        using var http = app.GetTestClient();
        var caseId = Guid.NewGuid();
        var path = $"/api/v1/cases/{caseId:D}/monitoring?offset=0&limit=100&version=7";
        using var anonymous = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(fake.Actor);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var success = await http.GetAsync(path + "&actorId=other");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.True(success.Headers.CacheControl?.NoStore);
        Assert.Equal("actor-1", fake.Actor);
        Assert.Equal(7, fake.Version);
        var client = new CaseMonitoringPageHttpClient(http);
        Assert.Equal(CaseMonitoringPageAccess.Found, (await client.GetAsync(caseId, 0, 100, 7)).Access);
        fake.Access = CaseMonitoringPageAccess.Stale;
        Assert.Equal(CaseMonitoringPageAccess.Stale, (await client.GetAsync(caseId, 0, 100, 7)).Access);
        fake.Access = CaseMonitoringPageAccess.NotFoundOrNotAuthorized;
        Assert.Equal(CaseMonitoringPageAccess.NotFoundOrNotAuthorized, (await client.GetAsync(caseId, 0, 100, 7)).Access);
        using var invalid = await http.GetAsync($"/api/v1/cases/{caseId:D}/monitoring?offset=1&limit=100");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed class FakeQuery : ICaseMonitoringPageQuery
    {
        public string? Actor { get; private set; }
        public long? Version { get; private set; }
        public CaseMonitoringPageAccess Access { get; set; } = CaseMonitoringPageAccess.Found;
        public Task<CaseMonitoringPageResult> GetAsync(ActorContext actor, Guid caseId, int offset, int limit,
            long? expectedVersion, CancellationToken cancellationToken)
        {
            Actor = actor.ActorId;
            Version = expectedVersion;
            return Task.FromResult(new CaseMonitoringPageResult(Access,
                Access == CaseMonitoringPageAccess.Found ? new CaseMonitoringPage(caseId, expectedVersion!.Value, [], null) : null));
        }
    }
}
