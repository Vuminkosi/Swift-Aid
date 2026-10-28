using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Intake;

namespace SwiftAid.Integration.Tests;

public sealed class IntakeCoordinationEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MappedRoutes_RequireAuthorizationAndCapActionBody()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<IIntakeCoordinationService>(new TestCoordinationService());
        builder.Services.AddSingleton<IIntakeCoordinationAuthorizer>(new TestCoordinationAuthorizer());
        builder.Services.AddSingleton(new IntakeCoordinationOptions());
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapIntakeCoordinationEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(candidate => candidate.RoutePattern.RawText is not null && candidate.RoutePattern.RawText.StartsWith(
                "/api/v1/intake/coordination", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, routes.Length);
        foreach (var route in routes)
        {
            var authorization = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(
                string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
                authorization.AuthenticationSchemes);
        }

        var actions = Assert.Single(routes,
            candidate => candidate.RoutePattern.RawText == "/api/v1/intake/coordination/{id}/actions");
        Assert.Equal(4096, actions.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    [Fact]
    public async Task AnonymousQueueRequest_IsUnauthorizedWithoutServices()
    {
        await using var host = await TestCoordinationHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/intake/coordination/queue");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Authorizer.QueueCalls);
        Assert.Equal(0, host.Service.QueueCalls);
    }

    [Fact]
    public async Task UnauthorizedCoordinator_GetsNotFoundWithoutServiceCall()
    {
        await using var host = await TestCoordinationHost.StartAsync(authorized: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/intake/coordination/queue");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, host.Authorizer.QueueCalls);
        Assert.Equal(0, host.Service.QueueCalls);
    }

    [Fact]
    public async Task Queue_ReturnsRedactedEntries()
    {
        await using var host = await TestCoordinationHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/intake/coordination/queue");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = JsonSerializer.Deserialize<List<IntakeCoordinationQueueEntry>>(body, JsonOptions);
        var entry = Assert.Single(entries!);
        Assert.Equal(TestCoordinationService.FixedReceiptId, entry.ReceiptId);
        Assert.True(entry.CountryCode.Length > 0);
    }

    [Fact]
    public async Task Queues_ReturnsEligibleOptions()
    {
        await using var host = await TestCoordinationHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/intake/coordination/queues");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var queues = JsonSerializer.Deserialize<List<IntakeCoordinationQueueOption>>(body, JsonOptions);
        var option = Assert.Single(queues!);
        Assert.Equal("queue-zw-harare", option.QueueId);
        Assert.Equal("org-zw-1", option.ReceivingOrganizationId);
    }

    [Fact]
    public async Task Queues_UnauthorizedCoordinator_GetsNotFound()
    {
        await using var host = await TestCoordinationHost.StartAsync(authorized: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/intake/coordination/queues");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("RoutedForReview")]
    [InlineData("Bogus")]
    public async Task Queue_InvalidRoutingFilter_IsRejected(string routing)
    {
        await using var host = await TestCoordinationHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/intake/coordination/queue?routingStatus={routing}");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.QueueCalls);
    }

    [Fact]
    public async Task Queue_InvalidLimit_IsRejected()
    {
        await using var host = await TestCoordinationHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/intake/coordination/queue?limit=0");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.QueueCalls);
    }

    [Fact]
    public async Task Detail_UnknownReceipt_GetsNotFound()
    {
        await using var host = await TestCoordinationHost.StartAsync(detail: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/intake/coordination/{Guid.NewGuid():D}");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(IntakeDecisionStatus.Applied, HttpStatusCode.OK)]
    [InlineData(IntakeDecisionStatus.Conflict, HttpStatusCode.Conflict)]
    [InlineData(IntakeDecisionStatus.Invalid, HttpStatusCode.BadRequest)]
    [InlineData(IntakeDecisionStatus.NotFoundOrNotAuthorized, HttpStatusCode.NotFound)]
    public async Task ActionOutcome_MapsToExpectedStatus(IntakeDecisionStatus status, HttpStatusCode expected)
    {
        await using var host = await TestCoordinationHost.StartAsync(serviceStatus: status);
        var receiptId = Guid.NewGuid();
        using var request = ActionRequest(receiptId, "ClaimForReview", null, "Taking review.");

        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, host.Service.ActionCalls);
        Assert.Equal(receiptId, host.Service.LastCommand!.ReceiptId);
        Assert.Equal(IntakeCoordinationAction.ClaimForReview, host.Service.LastCommand.Action);
        Assert.Equal("Taking review.", host.Service.LastCommand.Reason);
        Assert.Equal("actor-1", host.Service.LastActor!.ActorId);
    }

    [Fact]
    public void MapRequest_RejectsMalformedInput()
    {
        var receiptId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(Guid.Empty, new(1, 1, "ClaimForReview", null, "Reason.")));
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(receiptId, new(2, 1, "ClaimForReview", null, "Reason.")));
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 0, "ClaimForReview", null, "Reason.")));
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 1, "claimforreview", null, "Reason.")));
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 1, null, null, "Reason.")));
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 1, "ClaimForReview", null, null)));
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 1, "ClaimForReview", null, "  ")));
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 1, "AssignQueue", null, "Reason.")));
        Assert.Throws<ArgumentException>(() => IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 1, "ClaimForReview", "queue-a", "Reason.")));

        var claim = IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 3, "ClaimForReview", null, "Taking review."));
        Assert.Equal(new IntakeCoordinationCommand(receiptId, 3, IntakeCoordinationAction.ClaimForReview, null, "Taking review."), claim);

        var assign = IntakeCoordinationEndpoints.MapRequest(receiptId, new(1, 2, "AssignQueue", "queue-a", "Catchment confirmed."));
        Assert.Equal(new IntakeCoordinationCommand(receiptId, 2, IntakeCoordinationAction.AssignQueue, "queue-a", "Catchment confirmed."), assign);
    }

    [Fact]
    public void MapActionResult_CoversEveryStatus()
    {
        var receipt = new IntakeReceipt(Guid.NewGuid(), Guid.NewGuid(), IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);

        Assert.Equal(200, ((Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<IntakeReceipt>)IntakeCoordinationEndpoints.MapActionResult(new(IntakeDecisionStatus.Applied, receipt))).StatusCode);
        Assert.Equal(404, ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)IntakeCoordinationEndpoints.MapActionResult(new(IntakeDecisionStatus.NotFoundOrNotAuthorized, null))).StatusCode);
        Assert.Equal(409, ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)IntakeCoordinationEndpoints.MapActionResult(new(IntakeDecisionStatus.Conflict, null))).StatusCode);
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)IntakeCoordinationEndpoints.MapActionResult(new(IntakeDecisionStatus.Invalid, null))).StatusCode);
    }

    private static HttpRequestMessage ActionRequest(Guid receiptId, string action, string? queue, string? reason)
    {
        var payload = JsonSerializer.Serialize(
            new IntakeCoordinationActionRequest(1, 1, action, queue, reason), JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/intake/coordination/{receiptId:D}/actions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        TestAuthenticationHandler.AddCookieAuthentication(request);
        return request;
    }
}

internal sealed class TestCoordinationHost : IAsyncDisposable
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";
    private readonly WebApplication _application;
    private readonly IAntiforgery _antiforgery;
    private string? _antiforgeryCookie;

    private TestCoordinationHost(
        WebApplication application,
        HttpClient client,
        TestCoordinationService service,
        TestCoordinationAuthorizer authorizer)
    {
        _application = application;
        Client = client;
        Service = service;
        Authorizer = authorizer;
        _antiforgery = application.Services.GetRequiredService<IAntiforgery>();
    }

    public HttpClient Client { get; }

    public TestCoordinationService Service { get; }

    public TestCoordinationAuthorizer Authorizer { get; }

    public static async Task<TestCoordinationHost> StartAsync(
        bool authorized = true,
        IntakeDecisionStatus serviceStatus = IntakeDecisionStatus.Applied,
        IntakeCoordinationDetail? detail = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeader;
            options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        });
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

        var service = new TestCoordinationService(serviceStatus, detail);
        var authorizer = new TestCoordinationAuthorizer(authorized);
        builder.Services.AddSingleton<IIntakeCoordinationService>(service);
        builder.Services.AddSingleton<IIntakeCoordinationAuthorizer>(authorizer);
        builder.Services.AddSingleton(new IntakeCoordinationOptions
        {
            Enabled = true,
            CoordinationOrganizationId = "org-coord-1"
        });

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapIntakeCoordinationEndpoints();
        await application.StartAsync().ConfigureAwait(false);

        return new TestCoordinationHost(application, application.GetTestClient(), service, authorizer);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) =>
        Client.SendAsync(request);

    public Task<string> GetAntiforgeryAsync()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = _application.Services,
            User = TestAuthenticationHandler.CreatePrincipal(IdentityConstants.ApplicationScheme)
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");

        var tokens = _antiforgery.GetAndStoreTokens(context);
        var setCookie = context.Response.Headers["Set-Cookie"].ToString();
        _antiforgeryCookie = setCookie.Split(';', 2)[0];
        return Task.FromResult(tokens.RequestToken
            ?? throw new InvalidOperationException("The antiforgery token was not created."));
    }

    public Task<HttpResponseMessage> SendAuthenticatedAsync(HttpRequestMessage request, string? antiforgery)
    {
        var cookie = $"{TestAuthenticationHandler.CookieName}={TestAuthenticationHandler.CookieValue}";
        if (antiforgery is not null)
        {
            if (string.IsNullOrWhiteSpace(_antiforgeryCookie))
            {
                throw new InvalidOperationException("The antiforgery cookie was not created.");
            }

            cookie = $"{cookie}; {_antiforgeryCookie}";
            request.Headers.TryAddWithoutValidation(AntiforgeryHeader, antiforgery);
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookie);

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestCoordinationService(
    IntakeDecisionStatus status = IntakeDecisionStatus.Applied,
    IntakeCoordinationDetail? detail = null) : IIntakeCoordinationService
{
    public static readonly Guid FixedReceiptId = Guid.Parse("c3d9e5f6-2222-4e33-9bb9-23456789abcd");

    public int QueueCalls { get; private set; }

    public int ActionCalls { get; private set; }

    public ActorContext? LastActor { get; private set; }

    public IntakeCoordinationCommand? LastCommand { get; private set; }

    public Task<IReadOnlyList<IntakeCoordinationQueueEntry>> GetQueueAsync(
        ActorContext actor,
        IntakeRoutingStatus? routingStatus,
        int limit,
        DateTimeOffset? receivedBeforeUtc,
        Guid? receiptBefore,
        CancellationToken ct)
    {
        QueueCalls++;
        IReadOnlyList<IntakeCoordinationQueueEntry> entries =
        [
            new(FixedReceiptId, IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting,
                null, "ZW", "harare", "budiriro", "catalogue-1", IntakeSubjectScope.Human,
                DateTimeOffset.UtcNow, 1, null, false)
        ];
        return Task.FromResult(entries);
    }

    public Task<IntakeCoordinationDetail?> GetDetailAsync(ActorContext actor, Guid receiptId, CancellationToken ct) =>
        Task.FromResult(detail);

    public Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetEligibleQueuesAsync(ActorContext actor, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IntakeCoordinationQueueOption>>(
        [
            new("queue-zw-harare", "org-zw-1", "ZW", "catalogue-1")
        ]);

    public Task<IntakeDecisionResult> ActAsync(
        IntakeCoordinationCommand command,
        ActorContext actor,
        CancellationToken ct)
    {
        ActionCalls++;
        LastActor = actor;
        LastCommand = command;
        var receipt = status == IntakeDecisionStatus.Applied
            ? new IntakeReceipt(command.ReceiptId, Guid.NewGuid(), IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow)
            : null;
        return Task.FromResult(new IntakeDecisionResult(status, receipt));
    }
}

internal sealed class TestCoordinationAuthorizer(bool authorized = true) : IIntakeCoordinationAuthorizer
{
    private readonly bool _authorized = authorized;

    public int QueueCalls { get; private set; }

    public int ReportCalls { get; private set; }

    public Task<IntakeCoordinationAuthority?> AuthorizeAsync(ActorContext actor, CancellationToken ct)
    {
        QueueCalls++;
        return Task.FromResult(_authorized
            ? new IntakeCoordinationAuthority(actor.ActorId, "org-coord-1", "grant-1")
            : null);
    }

    public Task<IntakeCoordinationAuthority?> AuthorizeReportAsync(ActorContext actor, Guid receiptId, CancellationToken ct)
    {
        ReportCalls++;
        return Task.FromResult(_authorized
            ? new IntakeCoordinationAuthority(actor.ActorId, "org-coord-1", "grant-1")
            : null);
    }
}
