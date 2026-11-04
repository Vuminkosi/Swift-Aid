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

public sealed class CaseWorkDiscoveryEndpointTests
{
    [Fact]
    public async Task AuthenticatedActorAndContinuationAreUsedForDiscovery()
    {
        var query = new FakeQuery();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
          .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ICaseWorkDiscoveryQuery>(query);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCaseWorkDiscoveryEndpoints();
        await app.StartAsync();
        using var http = app.GetTestClient();
        const string path = "/api/v1/cases/monitoring/cases?kind=FollowUp&scope=Assigned&limit=1";
        using var anonymous = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(query.Actor);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        using var response = await http.GetAsync(path + "&actorId=someone-else");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("actor-1", query.Actor);
        var client = new CaseWorkDiscoveryHttpClient(http);
        var first = await client.GetAsync(CaseWorkDiscoveryKind.FollowUp, CaseWorkDiscoveryScope.Assigned, 1, null);
        Assert.Equal(CaseQueueAccess.Allowed, first.Access);
        Assert.Equal(FakeQuery.First, first.NextCursor);
        var second = await client.GetAsync(CaseWorkDiscoveryKind.FollowUp, CaseWorkDiscoveryScope.Assigned, 1, first.NextCursor);
        Assert.Equal(FakeQuery.Second, Assert.Single(second.CaseIds));
        Assert.Null(second.NextCursor);
        Assert.Equal(FakeQuery.First, query.Cursor);
        using var invalid = await http.GetAsync(path + "&cursor=bad");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed class FakeQuery : ICaseWorkDiscoveryQuery
    {
        public static Guid First { get; } = Guid.Parse("00000001-0000-0000-0000-000000000001");
        public static Guid Second { get; } = Guid.Parse("00000002-0000-0000-0000-000000000001");
        public string? Actor { get; private set; }
        public Guid? Cursor { get; private set; }
        public Task<CaseWorkDiscoveryPage> GetAsync(ActorContext actor, CaseWorkDiscoveryKind kind,
            CaseWorkDiscoveryScope scope, int limit, Guid? cursor, CancellationToken cancellationToken)
        {
            Actor = actor.ActorId;
            Cursor = cursor;
            return Task.FromResult(cursor is null
                ? new CaseWorkDiscoveryPage(CaseQueueAccess.Allowed, [First], First)
                : new CaseWorkDiscoveryPage(CaseQueueAccess.Allowed, [Second]));
        }
    }
}
