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
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class CaseCorrectionReadEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid CaseId = Guid.Parse("8a8bc63a-f23f-4e6e-96f6-63d4c1494b8a");

    [Fact]
    public void MappedCorrectionRoute_RequiresStaffAuthentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapCaseDetailEndpoints();

        var route = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/api/v1/cases/{caseId:guid}/correction");

        var authorization = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
            authorization.AuthenticationSchemes);
    }

    [Fact]
    public async Task AnonymousRequest_IsUnauthorizedWithoutCallingQuery()
    {
        var result = AllowedLegacy();
        await using var host = await TestCorrectionReadHost.StartAsync(result);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{CaseId:D}/correction");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Query.Calls);
    }

    [Fact]
    public async Task AllowedLegacyReport_ReturnsAuthorizedDataAndNoStoreHeaders()
    {
        var result = AllowedLegacy();
        await using var host = await TestCorrectionReadHost.StartAsync(result);
        using var request = AuthenticatedRequest();

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<CaseCorrectionReadData>(body, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(data);
        Assert.Equal(CaseId, data!.CaseId);
        Assert.Equal(4, data.AggregateVersion);
        Assert.Equal(2, data.ReportRevision);
        Assert.Equal(CaseCorrectionReportKind.LegacyV1, data.ReportKind);
        Assert.True(data.CanCorrectReport);
        Assert.Equal("synthetic authorized narrative", data.Narrative);
        Assert.Equal(CaseId, host.Query.LastCaseId);
        Assert.Equal("actor-1", host.Query.LastActor?.ActorId);
    }

    [Fact]
    public async Task DeniedResult_ReturnsGenericNotFoundWithoutNarrative()
    {
        await using var host = await TestCorrectionReadHost.StartAsync(
            new CaseCorrectionReadResult(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, null));
        using var request = AuthenticatedRequest();

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("synthetic authorized narrative", body, StringComparison.Ordinal);
        Assert.Contains("The case was not found.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableResult_ReturnsGenericServiceErrorWithoutNarrative()
    {
        await using var host = await TestCorrectionReadHost.StartAsync(
            new CaseCorrectionReadResult(CaseCorrectionReadAccess.Unavailable, null));
        using var request = AuthenticatedRequest();

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("synthetic authorized narrative", body, StringComparison.Ordinal);
        Assert.Contains("The case is temporarily unavailable.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowedResultWithoutData_ReturnsServiceError()
    {
        await using var host = await TestCorrectionReadHost.StartAsync(
            new CaseCorrectionReadResult(CaseCorrectionReadAccess.Allowed, null));
        using var request = AuthenticatedRequest();

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("synthetic authorized narrative", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryResolutionFailure_ReturnsGenericServiceErrorWithoutSensitiveDetails()
    {
        await using var host = await TestCorrectionReadHost.StartAsync(AllowedLegacy(), throwOnQueryResolution: true);
        using var request = AuthenticatedRequest();

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
        Assert.Contains("The case is temporarily unavailable.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic DI failure detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic authorized narrative", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedReport_ReturnsNoNarrativeAndNoCorrectionCapability()
    {
        var typed = new CaseCorrectionReadData(
            CaseId, 4, 2, CaseCorrectionReportKind.HumanV2, false, null);
        await using var host = await TestCorrectionReadHost.StartAsync(
            new CaseCorrectionReadResult(CaseCorrectionReadAccess.Allowed, typed));
        using var request = AuthenticatedRequest();

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<CaseCorrectionReadData>(body, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(data);
        Assert.Equal(CaseCorrectionReportKind.HumanV2, data!.ReportKind);
        Assert.False(data.CanCorrectReport);
        Assert.Null(data.Narrative);
        Assert.DoesNotContain("synthetic authorized narrative", body, StringComparison.Ordinal);
    }

    private static CaseCorrectionReadResult AllowedLegacy() => new(
        CaseCorrectionReadAccess.Allowed,
        new CaseCorrectionReadData(
            CaseId, 4, 2, CaseCorrectionReportKind.LegacyV1, true, "synthetic authorized narrative"));

    private static HttpRequestMessage AuthenticatedRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{CaseId:D}/correction");
        TestAuthenticationHandler.AddCookieAuthentication(request);
        return request;
    }
}

internal sealed class TestCorrectionReadHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    public HttpClient Client { get; }

    public TestCorrectionReadQuery Query { get; }

    private TestCorrectionReadHost(WebApplication application, HttpClient client, TestCorrectionReadQuery query)
    {
        _application = application;
        Client = client;
        Query = query;
    }

    public static async Task<TestCorrectionReadHost> StartAsync(
        CaseCorrectionReadResult result,
        bool throwOnQueryResolution = false)
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

        var query = new TestCorrectionReadQuery(result);
        if (throwOnQueryResolution)
        {
            builder.Services.AddScoped<ICaseCorrectionReadQuery>(
                _ => throw new InvalidOperationException("synthetic DI failure detail"));
        }
        else
        {
            builder.Services.AddSingleton<ICaseCorrectionReadQuery>(query);
        }

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapCaseDetailEndpoints();
        await application.StartAsync().ConfigureAwait(false);

        return new TestCorrectionReadHost(application, application.GetTestClient(), query);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestCorrectionReadQuery(CaseCorrectionReadResult result) : ICaseCorrectionReadQuery
{
    public int Calls { get; private set; }

    public Guid? LastCaseId { get; private set; }

    public ActorContext? LastActor { get; private set; }

    public Task<CaseCorrectionReadResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        LastActor = actor;
        LastCaseId = caseId;
        return Task.FromResult(result);
    }
}
