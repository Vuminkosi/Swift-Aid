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
using SwiftAid.Client.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class EligibleHealthOfficerEndpointTests
{
    [Fact]
    public async Task HttpClientUsesCaseRouteAndFailsClosedOnInvalidResults()
    {
        var handler = new CandidateHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
        var client = new EligibleHealthOfficerHttpClient(http);
        var caseId = Guid.NewGuid();
        var valid = await client.GetAsync(caseId);
        Assert.Equal(EligibleHealthOfficerLookupStatus.Available, valid.Status);
        Assert.Equal($"/api/v1/cases/{caseId:D}/eligible-health-officers", handler.Path);
        Assert.Equal("officer-id", Assert.Single(valid.Page!.Items).ActorId);

        handler.Body = "{}";
        Assert.Equal(EligibleHealthOfficerLookupStatus.Unavailable, (await client.GetAsync(caseId)).Status);
        handler.Body = "{\"items\":[]}";
        Assert.Equal(EligibleHealthOfficerLookupStatus.Unavailable, (await client.GetAsync(caseId)).Status);
        handler.Body = "{\"items\":[{\"designationId\":\"00000000-0000-0000-0000-000000000000\",\"designationVersion\":1,\"actorId\":\"x\"}],\"hasMore\":false}";
        Assert.Equal(EligibleHealthOfficerLookupStatus.Unavailable, (await client.GetAsync(caseId)).Status);
        handler.Status = HttpStatusCode.NotFound;
        Assert.Equal(EligibleHealthOfficerLookupStatus.Denied, (await client.GetAsync(caseId)).Status);
    }

    private sealed class CandidateHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{\"items\":[{\"designationId\":\"d2df7b40-00f4-4f63-84a2-7fa45f1cb761\",\"designationVersion\":1,\"actorId\":\"officer-id\",\"displayLabel\":\"Officer\"}],\"hasMore\":false}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

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
