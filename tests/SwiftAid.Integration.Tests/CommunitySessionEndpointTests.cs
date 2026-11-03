using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Sessions;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Gateway.Sessions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Community-session endpoints: fail-closed officer authorization and public
/// session behavior. Fake-service style; no live database.
/// </summary>
public sealed class CommunitySessionEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid KnownSessionId = Guid.Parse("d0c00001-2222-4a44-9aa9-123456789abc");

    [Theory]
    [InlineData("/public/v1/sessions/", HttpStatusCode.OK)]
    [InlineData("/public/v1/sessions/search", HttpStatusCode.OK)]
    [InlineData("/public/v1/sessions/d0c00001-2222-4a44-9aa9-123456789abc", HttpStatusCode.OK)]
    [InlineData("/public/v1/sessions/d0c00001-2222-4a44-9aa9-123456789abc/calendar", HttpStatusCode.OK)]
    [InlineData("/public/v1/sessions/d0c00001-2222-4a44-9aa9-123456789abc/invitation", HttpStatusCode.OK)]
    [InlineData("/public/v1/sessions/00000000-0000-0000-0000-000000000001", HttpStatusCode.NotFound)]
    public async Task PublicSessionResponsesAreNotStoredByHttpCaches(string path, HttpStatusCode expected)
    {
        await using var host = await TestSessionHost.StartAsync(withPublished: true);
        using var response = await host.Client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.CacheControl?.Public ?? false);
    }

    [Fact]
    public async Task PublicDetailRechecksVisibilityAfterPublicationChanges()
    {
        await using var host = await TestSessionHost.StartAsync(withPublished: true);
        var path = $"/public/v1/sessions/{KnownSessionId:D}";
        using var first = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);

        host.Service.WithPublished = false;
        using var next = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, next.StatusCode);
        Assert.True(next.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public void OfficerRoutesRequireAuthenticationAndScopedCapabilityMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapCommunitySessionEndpoints();

        var expectedSchemes = string.Join(",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(candidate => (candidate.RoutePattern.RawText ?? string.Empty).StartsWith(
                "/api/v1/sessions", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(6, routes.Length);
        foreach (var route in routes)
        {
            var authorization = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(expectedSchemes, authorization.AuthenticationSchemes);
            Assert.NotNull(route.Metadata.GetMetadata<CommunitySessionScopedAuthorizationRequiredMetadata>());
        }
    }

    [Fact]
    public void PublicRoutes_AllowAnonymous()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapPublicSessionEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(candidate => (candidate.RoutePattern.RawText ?? string.Empty).StartsWith(
                "/public/v1/sessions", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(5, routes.Length);
        foreach (var route in routes)
        {
            Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        }
    }

    [Fact]
    public async Task AnonymousCreate_IsUnauthorized()
    {
        await using var host = await TestSessionHost.StartAsync();
        using var request = CreateJsonRequest(HttpMethod.Post, "/api/v1/sessions", ValidCreateRequest());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task CookieWithoutAntiforgeryToken_IsRejected()
    {
        await using var host = await TestSessionHost.StartAsync();
        using var request = CreateJsonRequest(HttpMethod.Post, "/api/v1/sessions", ValidCreateRequest());
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task CreateDraft_IsDeniedWithoutScopedStaffCapability()
    {
        await using var host = await TestSessionHost.StartAsync();
        using var request = CreateJsonRequest(HttpMethod.Post, "/api/v1/sessions", ValidCreateRequest());

        using var response = await host.SendBearerAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task CreateDraft_WithoutTitle_IsRejected()
    {
        await using var host = await TestSessionHost.StartAsync();
        var request = ValidCreateRequest() with { Title = " " };
        using var message = CreateJsonRequest(HttpMethod.Post, "/api/v1/sessions", request);

        using var response = await host.SendBearerAsync(message);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task Update_WithZeroVersion_IsRejected()
    {
        await using var host = await TestSessionHost.StartAsync();
        var request = ValidUpdateRequest() with { ExpectedVersion = 0 };
        using var message = CreateJsonRequest(HttpMethod.Put, $"/api/v1/sessions/{KnownSessionId:D}", request);

        using var response = await host.SendBearerAsync(message);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task Update_IsDeniedWithoutScopedStaffCapability()
    {
        await using var host = await TestSessionHost.StartAsync();
        using var message = CreateJsonRequest(HttpMethod.Put, $"/api/v1/sessions/{KnownSessionId:D}", ValidUpdateRequest());

        using var response = await host.SendBearerAsync(message);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task Review_IsDeniedWithoutScopedStaffCapability()
    {
        await using var host = await TestSessionHost.StartAsync();
        host.Service.OnExecute = (_, _) =>
            throw new InvalidOperationException("The expected version 2 does not match the current version 3.");
        using var message = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/v1/sessions/{KnownSessionId:D}/review",
            new ReviewSessionApiRequest(2, false, "second look"));

        using var response = await host.SendBearerAsync(message);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task Schedule_IsDeniedWithoutScopedStaffCapability()
    {
        await using var host = await TestSessionHost.StartAsync();
        host.Service.OnExecute = (_, _) =>
            throw new InvalidOperationException("A session can only be scheduled for a future start.");
        using var message = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/v1/sessions/{KnownSessionId:D}/schedule",
            new ScheduleSessionApiRequest(2, "publish now"));

        using var response = await host.SendBearerAsync(message);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task Cancel_WithoutReason_IsRejected()
    {
        await using var host = await TestSessionHost.StartAsync();
        using var message = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/v1/sessions/{KnownSessionId:D}/cancel",
            new CancelSessionApiRequest(2, " ", "venue closed"));

        using var response = await host.SendBearerAsync(message);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task Cancel_IsDeniedWithoutScopedStaffCapability()
    {
        await using var host = await TestSessionHost.StartAsync();
        using var message = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/v1/sessions/{KnownSessionId:D}/cancel",
            new CancelSessionApiRequest(2, "venue flooded", "venue closed"));

        using var response = await host.SendBearerAsync(message);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task Complete_IsDeniedWithoutScopedStaffCapability()
    {
        await using var host = await TestSessionHost.StartAsync();
        using var message = CreateJsonRequest(
            HttpMethod.Post,
            $"/api/v1/sessions/{KnownSessionId:D}/complete",
            new CompleteSessionApiRequest(3, "session held"));

        using var response = await host.SendBearerAsync(message);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Service.ExecuteCalls);
    }

    [Fact]
    public async Task InProcessClient_DeniesStaffReadAndMutationWithoutCallingService()
    {
        var service = new FakeCommunitySessionService();
        using var services = new ServiceCollection()
            .AddSingleton<ICommunitySessionService>(service)
            .BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "actor-1"),
                new Claim("swiftaid_access_epoch", "1")], "test"));
        var client = new SessionGatewayClient(
            new CommunityFixedAuthenticationStateProvider(principal), services);

        var staffView = await client.GetStaffAsync(KnownSessionId);
        var outcome = await client.ExecuteAsync(new CancelSessionCommand(
            KnownSessionId, 1, "venue flooded", "venue closed", DateTimeOffset.UtcNow));

        Assert.Null(staffView);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, service.StaffReadCalls);
        Assert.Equal(0, service.ExecuteCalls);
    }

    [Fact]
    public async Task ServerPublicDetail_DistinguishesUnavailableAndUnknown()
    {
        using var noServices = new ServiceCollection().BuildServiceProvider();
        var unavailable = new WebPublicCommunitySessionClient(new SessionGatewayClient(
            new CommunityFixedAuthenticationStateProvider(new ClaimsPrincipal()), noServices));
        using var services = new ServiceCollection()
            .AddSingleton<ICommunitySessionService>(new FakeCommunitySessionService())
            .BuildServiceProvider();
        var available = new WebPublicCommunitySessionClient(new SessionGatewayClient(
            new CommunityFixedAuthenticationStateProvider(new ClaimsPrincipal()), services));

        var failed = await unavailable.GetPublicAsync(KnownSessionId);
        var missing = await available.GetPublicAsync(KnownSessionId);

        Assert.Equal(UiCapabilityResultCode.Unavailable, failed.Code);
        Assert.Equal(UiCapabilityResultCode.Success, missing.Code);
        Assert.Null(missing.Value);
    }

    [Fact]
    public async Task ServerPublicClient_MissingService_IsUnavailableRatherThanEmpty()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var gateway = new SessionGatewayClient(
            new CommunityFixedAuthenticationStateProvider(new ClaimsPrincipal()), services);
        var client = new WebPublicCommunitySessionClient(gateway);

        var result = await client.ListPublicAsync();

        Assert.Equal(UiCapabilityResultCode.Unavailable, result.Code);
        Assert.False(result.Metadata.IsAvailable);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ServerPublicClient_ServiceFailure_IsUnavailableRatherThanEmpty()
    {
        var service = new FakeCommunitySessionService { FailList = true };
        using var services = new ServiceCollection().AddSingleton<ICommunitySessionService>(service).BuildServiceProvider();
        var gateway = new SessionGatewayClient(
            new CommunityFixedAuthenticationStateProvider(new ClaimsPrincipal()), services);
        var client = new WebPublicCommunitySessionClient(gateway);

        var result = await client.ListPublicAsync();

        Assert.Equal(UiCapabilityResultCode.Unavailable, result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ServerPublicClient_EmptyServiceResult_IsSuccessful()
    {
        using var services = new ServiceCollection()
            .AddSingleton<ICommunitySessionService>(new FakeCommunitySessionService())
            .BuildServiceProvider();
        var gateway = new SessionGatewayClient(
            new CommunityFixedAuthenticationStateProvider(new ClaimsPrincipal()), services);
        var client = new WebPublicCommunitySessionClient(gateway);

        var result = await client.ListPublicAsync();

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<PublicSessionView>>(result.Value));
    }

    [Fact]
    public async Task PublicSearch_UsesPublicProjectionAndPassesFilters()
    {
        await using var host = await TestSessionHost.StartAsync(withPublished: true);
        using var response = await host.Client.GetAsync("/public/v1/sessions/search?search=water&country=ZW&category=Nutrition&language=en&status=Completed&localFrom=2026-11-01&limit=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(PublicSessionQueryFeatures.StatusFilter, response.Headers.GetValues(PublicSessionQueryFeatures.HeaderName));
        var page = JsonSerializer.Deserialize<PublicSessionPage>(await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(KnownSessionId, Assert.Single(page.Items).SessionId);
        Assert.Equal("water", host.Service.LastQuery?.Search);
        Assert.Equal("ZW", host.Service.LastQuery?.CountryCode);
        Assert.Equal("Nutrition", host.Service.LastQuery?.Category);
        Assert.Equal("en", host.Service.LastQuery?.Language);
        Assert.Equal("Completed", host.Service.LastQuery?.Status);
        Assert.Equal(new DateOnly(2026, 11, 1), host.Service.LastQuery?.LocalDateFrom);
        Assert.Equal(1, host.Service.LastQuery?.Limit);
    }

    [Fact]
    public async Task PublicSearch_StatusCapabilityIsUnderstoodByTheHttpClient()
    {
        await using var host = await TestSessionHost.StartAsync(withPublished: true);
        var client = new CommunitySessionHttpClient(host.Client);

        var result = await client.QueryPublicResultAsync(new PublicSessionQuery(Status: "Scheduled"));

        Assert.True(result.Succeeded);
        Assert.Equal("Scheduled", host.Service.LastQuery?.Status);
        Assert.NotNull(result.Value);
    }

    [Theory]
    [InlineData("afterId=not-a-guid")]
    [InlineData("from=not-a-date")]
    [InlineData("limit=invalid")]
    [InlineData("localFrom=11-01-2026")]
    public async Task PublicSearch_RejectsMalformedParameters(string query)
    {
        await using var host = await TestSessionHost.StartAsync();
        using var response = await host.Client.GetAsync($"/public/v1/sessions/search?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DisabledPipeline_OfficerRoute_GetsNotFound()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
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
        // No ICommunitySessionService: the session pipeline is disabled here.
        using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCommunitySessionEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = CreateJsonRequest(HttpMethod.Post, "/api/v1/sessions", ValidCreateRequest());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("/public/v1/sessions?limit=0")]
    [InlineData("/public/v1/sessions?limit=-4")]
    [InlineData("/public/v1/sessions?limit=101")]
    [InlineData("/public/v1/sessions?limit=not-a-number")]
    public async Task PublicList_InvalidLimit_IsRejected(string address)
    {
        await using var host = await TestSessionHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, address);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.ListCalls);
    }

    [Fact]
    public async Task PublicList_ReturnsOkEmptyList()
    {
        await using var host = await TestSessionHost.StartAsync(items: []);

        using var response = await host.Client.GetAsync("/public/v1/sessions");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = JsonSerializer.Deserialize<IReadOnlyList<PublicSessionView>>(body, JsonOptions);
        Assert.NotNull(sessions);
        Assert.Empty(sessions!);
    }

    [Fact]
    public async Task PublicList_PassesLimit()
    {
        await using var host = await TestSessionHost.StartAsync();

        using var response = await host.Client.GetAsync("/public/v1/sessions?limit=25");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(25, host.Service.LastLimit);
    }

    [Fact]
    public async Task PublicGet_UnknownSession_GetsNotFound()
    {
        await using var host = await TestSessionHost.StartAsync();

        using var response = await host.Client.GetAsync($"/public/v1/sessions/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicGet_PublishedSession_ReturnsProjection()
    {
        await using var host = await TestSessionHost.StartAsync(withPublished: true);

        using var response = await host.Client.GetAsync($"/public/v1/sessions/{KnownSessionId:D}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = JsonSerializer.Deserialize<PublicSessionView>(body, JsonOptions);
        Assert.NotNull(session);
        Assert.Equal("Dry-season holding hygiene", session!.Title);
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("createdByActorId", out _));
        Assert.False(document.RootElement.TryGetProperty("reviewedByActorId", out _));
        Assert.False(document.RootElement.TryGetProperty("selfReviewAcknowledged", out _));
        Assert.False(document.RootElement.TryGetProperty("attendees", out _));
    }

    [Fact]
    public async Task PublicCalendar_ReturnsCalendarFile()
    {
        await using var host = await TestSessionHost.StartAsync(withPublished: true);

        using var response = await host.Client.GetAsync($"/public/v1/sessions/{KnownSessionId:D}/calendar");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"UID:swiftaid-community-session-{KnownSessionId:D}", body);
        Assert.Contains("SEQUENCE:1", body);
        Assert.Contains($"session-{KnownSessionId:D}.ics",
            response.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicCalendar_UnknownSession_GetsNotFound()
    {
        await using var host = await TestSessionHost.StartAsync();

        using var response = await host.Client.GetAsync($"/public/v1/sessions/{Guid.NewGuid():D}/calendar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicInvitation_ReturnsLogisticsText()
    {
        await using var host = await TestSessionHost.StartAsync(withPublished: true);

        using var response = await host.Client.GetAsync($"/public/v1/sessions/{KnownSessionId:D}/invitation");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Dry-season holding hygiene", body);
        Assert.Contains("officer-2", body);
    }

    [Fact]
    public async Task PublicInvitation_UnknownSession_GetsNotFound()
    {
        await using var host = await TestSessionHost.StartAsync();

        using var response = await host.Client.GetAsync($"/public/v1/sessions/{Guid.NewGuid():D}/invitation");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DisabledPipeline_PublicRoute_GetsNotFound()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        // No ICommunitySessionService: the session pipeline is disabled here.
        using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPublicSessionEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/public/v1/sessions");
        using var detailResponse = await client.GetAsync($"/public/v1/sessions/{KnownSessionId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void MapResult_CoversOutcomes()
    {
        var ok = CommunitySessionEndpoints.MapResult(
            new SessionCommandResult(KnownSessionId, 1, "Draft"));
        Assert.Equal(200,
            ((Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<SessionCommandResult>)ok).StatusCode);

        var unavailable = CommunitySessionEndpoints.MapResult(
            new SessionCommandResult(Guid.Empty, 0, "Draft"));
        Assert.Equal(503,
            ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)unavailable).StatusCode);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), 400)]
    [InlineData(typeof(UnauthorizedAccessException), 404)]
    [InlineData(typeof(TimeoutException), 503)]
    public void MapCommandException_CoversSimpleCases(Type exceptionType, int statusCode)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "failure")!;
        var result = CommunitySessionEndpoints.MapCommandException(exception);

        Assert.Equal(statusCode,
            ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)result).StatusCode);
    }

    [Fact]
    public void MapCommandException_VersionConflict_Is409()
    {
        var result = CommunitySessionEndpoints.MapCommandException(
            new InvalidOperationException("The expected version 2 does not match the current version 3."));

        Assert.Equal(409,
            ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)result).StatusCode);
    }

    [Fact]
    public void MapCommandException_StateViolation_Is422()
    {
        var result = CommunitySessionEndpoints.MapCommandException(
            new InvalidOperationException("A session can only be scheduled for a future start."));

        Assert.Equal(422,
            ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)result).StatusCode);
    }

    [Fact]
    public void MapCreateRequest_BlankTitle_IsRejectedBeforeCommandCreation()
    {
        var request = ValidCreateRequest() with { Title = " " };

        var exception = Assert.Throws<ArgumentException>(
            () => CommunitySessionEndpoints.MapCreateRequest(request));

        Assert.Equal("Title", exception.ParamName);
    }

    [Fact]
    public void MapUpdateRequest_ZeroExpectedVersion_IsRejectedBeforeCommandCreation()
    {
        var request = ValidUpdateRequest() with { ExpectedVersion = 0 };

        var exception = Assert.Throws<ArgumentException>(
            () => CommunitySessionEndpoints.MapUpdateRequest(KnownSessionId, request));

        Assert.Equal("expectedVersion", exception.ParamName);
    }

    [Fact]
    public void MapCancelRequest_BlankCancellationReason_IsRejectedBeforeCommandCreation()
    {
        var request = new CancelSessionApiRequest(2, " ", "venue closed");

        var exception = Assert.Throws<ArgumentException>(
            () => CommunitySessionEndpoints.MapCancelRequest(KnownSessionId, request));

        Assert.Equal("CancellationReason", exception.ParamName);
    }

    private static CreateSessionApiRequest ValidCreateRequest() => new(
        "Dry-season holding hygiene",
        "Practical steps for clean holding areas.",
        "Livestock keepers near Harare showgrounds",
        ["hygiene"],
        "ZW",
        "region-zw-harare",
        "Animal",
        "officer-2",
        "en",
        new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero),
        "Africa/Harare",
        60,
        40,
        [new SessionMaterialApiRef("mat-hygiene-1", "v2")],
        "Public",
        "None",
        null,
        [24, 1],
        "planned for review");

    private static UpdateSessionApiRequest ValidUpdateRequest() => new(
        3,
        "Dry-season holding hygiene",
        "Practical steps for clean holding areas.",
        "Livestock keepers near Harare showgrounds",
        ["hygiene"],
        "officer-2",
        "en",
        new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero),
        60,
        40,
        [new SessionMaterialApiRef("mat-hygiene-1", "v2")],
        "Public",
        "None",
        null,
        [24, 1],
        "corrected summary");

    private static HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string address, T body) =>
        new(method, address)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };

    private static PublicSessionView PublishedView() => new(
        KnownSessionId,
        3,
        "Dry-season holding hygiene",
        "Practical steps for clean holding areas.",
        "Livestock keepers near Harare showgrounds",
        ["hygiene"],
        "ZW",
        "region-zw-harare",
        "Animal",
        "officer-2",
        "en",
        new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero),
        "Africa/Harare",
        60,
        40,
        [new StoredSessionMaterial("mat-hygiene-1", "v2")],
        SessionChannelType.None,
        null,
        "Scheduled",
        null,
        1);

    private const string PublishedIcs =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//SwiftAid//Community Session//EN\r\nBEGIN:VEVENT\r\n" +
        "UID:swiftaid-community-session-d0c00001-2222-4a44-9aa9-123456789abc\r\n" +
        "DTSTAMP:20261028T080000Z\r\nDTSTART:20261105T100000Z\r\nDURATION:PT1H0M\r\n" +
        "SUMMARY:Dry-season holding hygiene\r\nSEQUENCE:1\r\nSTATUS:CONFIRMED\r\nTRANSP:OPAQUE\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private const string PublishedInvitation =
        "You are invited: Dry-season holding hygiene\r\nWhen: Thursday, 5 November 2026 12:00 (Africa/Harare)\r\n" +
        "Duration: 60 minutes\r\nPresenter: officer-2\r\nWhere: region-zw-harare, ZW\r\n" +
        "Practical steps for clean holding areas.\r\n";

    private sealed class FakeCommunitySessionService : ICommunitySessionService
    {
        public int ExecuteCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int StaffReadCalls { get; private set; }
        public int? LastLimit { get; private set; }
        public SessionCommand? LastCommand { get; private set; }
        public string? LastActorId { get; private set; }

        public Func<SessionCommand, string, SessionCommandResult>? OnExecute { get; set; }
        public bool WithPublished { get; set; }
        public bool FailList { get; set; }
        public PublicSessionQuery? LastQuery { get; private set; }

        public Task<SessionCommandResult> ExecuteAsync(SessionCommand command, string actorId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            LastCommand = command;
            LastActorId = actorId;
            if (OnExecute is not null)
            {
                return Task.FromResult(OnExecute(command, actorId));
            }

            return Task.FromResult(new SessionCommandResult(
                command.SessionId, command.ExpectedVersion + 1, "Draft"));
        }

        public Task<CommunitySessionView?> GetStaffAsync(Guid sessionId, string actorId, CancellationToken ct)
        {
            StaffReadCalls++;
            return Task.FromResult<CommunitySessionView?>(null);
        }

        public Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(int? limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailList)
            {
                throw new InvalidOperationException("Test service failure.");
            }
            ListCalls++;
            LastLimit = limit;
            IReadOnlyList<PublicSessionView> sessions =
                WithPublished ? [CommunitySessionEndpointTests.PublishedView()] : [];
            return Task.FromResult(sessions);
        }

        public Task<PublicSessionView?> GetPublicAsync(Guid sessionId, CancellationToken ct) =>
            Task.FromResult<PublicSessionView?>(
                WithPublished && sessionId == CommunitySessionEndpointTests.KnownSessionId
                    ? CommunitySessionEndpointTests.PublishedView()
                    : null);

        public Task<PublicSessionPage> QueryPublicAsync(PublicSessionQuery query, CancellationToken ct)
        {
            LastQuery = query;
            IReadOnlyList<PublicSessionView> items = WithPublished ? [PublishedView()] : [];
            return Task.FromResult(new PublicSessionPage(items, null));
        }

        public Task<string?> GetIcsAsync(Guid sessionId, DateTimeOffset generatedAtUtc, CancellationToken ct) =>
            Task.FromResult<string?>(
                WithPublished && sessionId == CommunitySessionEndpointTests.KnownSessionId
                    ? PublishedIcs
                    : null);

        public Task<string?> GetInvitationTextAsync(Guid sessionId, CancellationToken ct) =>
            Task.FromResult<string?>(
                WithPublished && sessionId == CommunitySessionEndpointTests.KnownSessionId
                    ? PublishedInvitation
                    : null);
    }

    private sealed class TestSessionHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        public HttpClient Client { get; }

        public FakeCommunitySessionService Service { get; }

        private TestSessionHost(WebApplication application, HttpClient client, FakeCommunitySessionService service)
        {
            _application = application;
            Client = client;
            Service = service;
        }

        public static async Task<TestSessionHost> StartAsync(
            IReadOnlyList<PublicSessionView>? items = null, bool withPublished = false)
        {
            var service = new FakeCommunitySessionService { WithPublished = withPublished };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAntiforgery();
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
            builder.Services.AddSingleton<ICommunitySessionService>(service);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCommunitySessionEndpoints();
            app.MapPublicSessionEndpoints();
            await app.StartAsync().ConfigureAwait(false);

            return new TestSessionHost(app, app.GetTestClient(), service);
        }

        public Task<HttpResponseMessage> SendBearerAsync(HttpRequestMessage request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}

internal sealed class CommunityFixedAuthenticationStateProvider(ClaimsPrincipal principal)
    : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(principal));
}
