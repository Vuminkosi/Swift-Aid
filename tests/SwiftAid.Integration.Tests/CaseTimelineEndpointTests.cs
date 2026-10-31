using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class CaseTimelineEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MappedTimelineRoute_RequiresStaffAuthentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ICaseTimelineQuery>(new TestTimelineQuery(found: true));
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapCaseTimelineEndpoints();

        var route = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/api/v1/cases/{caseId:guid}/timeline");

        var authorization = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
            authorization.AuthenticationSchemes);
    }

    [Fact]
    public async Task AnonymousTimelineRequest_IsUnauthorized()
    {
        await using var host = await TestTimelineHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{Guid.NewGuid():D}/timeline");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task DeniedTimeline_GetsNotFound()
    {
        await using var host = await TestTimelineHost.StartAsync(found: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{Guid.NewGuid():D}/timeline");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, host.Service.Calls);
    }

    [Fact]
    public async Task FoundTimeline_ReturnsHeaderAndOrderedEntries()
    {
        await using var host = await TestTimelineHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/cases/{TestTimelineQuery.FixedCaseId:D}/timeline?limit=50&beforeAggregateVersion=2");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var timeline = JsonSerializer.Deserialize<CaseTimeline>(body, JsonOptions);
        Assert.NotNull(timeline);
        Assert.Equal(TestTimelineQuery.FixedCaseId, timeline!.Header.CaseId);
        Assert.Equal(2, timeline.Entries.Count);
        Assert.Equal(50, host.Service.LastLimit);
        Assert.Equal(2, host.Service.LastBeforeAggregateVersion);
        Assert.DoesNotContain("ProtectedBytes", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("not-a-number")]
    public async Task InvalidHistoryCursor_IsRejected(string cursor)
    {
        await using var host = await TestTimelineHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/cases/{TestTimelineQuery.FixedCaseId:D}/timeline?beforeAggregateVersion={cursor}");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("not-a-number")]
    public async Task InvalidLimit_IsRejected(string limit)
    {
        await using var host = await TestTimelineHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/cases/{Guid.NewGuid():D}/timeline?limit={limit}");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task OversizedLimit_IsClampedToServiceMaximum()
    {
        await using var host = await TestTimelineHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/cases/{Guid.NewGuid():D}/timeline?limit=9999");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CaseTimelineQuery.MaxLimit, host.Service.LastLimit);
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
        builder.Services.AddAuthorization();
        // No ICaseTimelineQuery: the case command pipeline is disabled here.
        using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCaseTimelineEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/cases/{Guid.NewGuid():D}/timeline");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task EmptyCaseId_IsRejected()
    {
        await using var host = await TestTimelineHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/cases/00000000-0000-0000-0000-000000000000/timeline");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }
}

internal sealed class TestTimelineHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    public HttpClient Client { get; }

    public TestTimelineQuery Service { get; }

    private TestTimelineHost(WebApplication application, HttpClient client, TestTimelineQuery service)
    {
        _application = application;
        Client = client;
        Service = service;
    }

    public static async Task<TestTimelineHost> StartAsync(bool found = true)
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
        builder.Services.AddAuthorization();

        var service = new TestTimelineQuery(found);
        builder.Services.AddSingleton<ICaseTimelineQuery>(service);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapCaseTimelineEndpoints();
        await application.StartAsync().ConfigureAwait(false);

        return new TestTimelineHost(application, application.GetTestClient(), service);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestTimelineQuery(bool found) : ICaseTimelineQuery
{
    public static readonly Guid FixedCaseId = Guid.Parse("a11ce000-1111-4c22-9dd9-123456789abc");

    public int Calls { get; private set; }

    public int? LastLimit { get; private set; }

    public long? LastBeforeAggregateVersion { get; private set; }

    public Task<CaseTimelineResult> GetAsync(
        ActorContext actor,
        Guid caseId,
        int? limit,
        CancellationToken ct,
        long? beforeAggregateVersion = null)
    {
        Calls++;
        LastLimit = limit;
        LastBeforeAggregateVersion = beforeAggregateVersion;
        if (!found)
        {
            return Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null));
        }

        var header = new CaseTimelineHeader(
            caseId, "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 3, 1,
            new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var entries = new List<CaseTimelineEntry>
        {
            new(Guid.NewGuid(), "CaseReported", "Report accepted", CaseTimelineEntryKind.Authoritative,
                1, 1, Guid.NewGuid(), "officer-1", null,
                new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), 1),
            new(Guid.NewGuid(), "CaseResolved", "Case resolved", CaseTimelineEntryKind.Authoritative,
                2, 1, Guid.NewGuid(), "officer-1",
                new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 28, 9, 5, 0, TimeSpan.Zero), 1)
        };
        return Task.FromResult(new CaseTimelineResult(
            CaseTimelineAccess.Found, new CaseTimeline(header, entries)));
    }
}
