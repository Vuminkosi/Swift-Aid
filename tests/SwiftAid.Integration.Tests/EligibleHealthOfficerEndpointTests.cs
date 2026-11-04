using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class EligibleHealthOfficerEndpointTests
{
    [Fact]
    public async Task EndpointRequiresAuthenticationBoundsAndCurrentCaseAuthority()
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
        var query = new CandidateQuery();
        builder.Services.AddSingleton<IEligibleHealthOfficerQuery>(query);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEligibleHealthOfficerEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var caseId = Guid.NewGuid();
        var path = $"/api/v1/cases/{caseId:D}/eligible-health-officers";

        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, query.Calls);

        async Task<HttpResponseMessage> Get(string suffix)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path + suffix);
            TestAuthenticationHandler.AddCookieAuthentication(request);
            return await client.SendAsync(request);
        }

        foreach (var invalid in new[] { "?limit=0", "?limit=21", "?limit=oops", "?limit=1&limit=2" })
        {
            using var response = await Get(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, query.Calls);
        using var allowed = await Get("?limit=2");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True(allowed.Headers.CacheControl!.NoStore);
        var page = await allowed.Content.ReadFromJsonAsync<EligibleHealthOfficerCandidatePage>();
        Assert.True(page!.HasMore);
        var candidate = Assert.Single(page.Items);
        Assert.Equal(query.DesignationId, candidate.DesignationId);
        Assert.Equal(3, candidate.DesignationVersion);
        Assert.Equal(caseId, query.CaseId);
        Assert.Equal(2, query.Limit);
        Assert.NotNull(query.Actor);

        query.Denied = true;
        using var denied = await Get("");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl!.NoStore);
    }

    private sealed class CandidateQuery : IEligibleHealthOfficerQuery
    {
        public int Calls { get; private set; }
        public ActorContext? Actor { get; private set; }
        public Guid CaseId { get; private set; }
        public int Limit { get; private set; }
        public bool Denied { get; set; }
        public Guid DesignationId { get; } = Guid.NewGuid();

        public Task<EligibleHealthOfficerPage?> GetAsync(ActorContext actor, Guid caseId, int limit, CancellationToken ct)
        {
            Calls++; Actor = actor; CaseId = caseId; Limit = limit;
            return Task.FromResult<EligibleHealthOfficerPage?>(Denied ? null :
                new([new(DesignationId, 3, "officer-id", "Officer")], true));
        }
    }
}
