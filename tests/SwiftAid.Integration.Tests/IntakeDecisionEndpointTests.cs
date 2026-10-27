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

namespace SwiftAid.Integration.Tests;

public sealed class IntakeDecisionEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MappedRoute_RequiresAuthorizationAndCapsRequestBody()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<IIntakeService>(new TestDecisionService());
        builder.Services.AddSingleton<IIntakeReviewAuthorizer>(new TestDecisionAuthorizer());
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapIntakeDecisionEndpoints();

        var endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/api/v1/intake/{id}/decisions");

        var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
            authorization.AuthenticationSchemes);
        Assert.Equal(4096, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    [Fact]
    public async Task AnonymousMalformedRequest_IsUnauthorizedWithoutCallingServices()
    {
        await using var host = await TestDecisionHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/intake/{Guid.NewGuid()}/decisions")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Authorizer.Calls);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task CookieRequestWithoutAntiforgery_IsRejectedBeforeAuthorization()
    {
        await using var host = await TestDecisionHost.StartAsync();
        using var request = DecisionRequest(Guid.NewGuid(), "Decline", 1, null);
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Authorizer.Calls);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task OversizedBearerRequest_IsRejectedBeforeJsonParsing()
    {
        await using var host = await TestDecisionHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/intake/{Guid.NewGuid()}/decisions")
        {
            Content = new ByteArrayContent(new byte[4096 + 1])
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Authorizer.Calls);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task UnauthorizedReviewer_GetsNotFoundWithoutCallingService()
    {
        await using var host = await TestDecisionHost.StartAsync(authorized: false);
        using var request = DecisionRequest(Guid.NewGuid(), "Decline", 1, null);

        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, host.Authorizer.Calls);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task UnknownDecisionKind_IsInvalidWithoutCallingService()
    {
        await using var host = await TestDecisionHost.StartAsync();
        using var request = DecisionRequest(Guid.NewGuid(), "PromoteToPresident", 1, null);

        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Authorizer.Calls);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task NonGuidRouteId_IsInvalidWithoutCallingServices()
    {
        await using var host = await TestDecisionHost.StartAsync();
        using var request = DecisionRequest("not-a-guid", "Decline", 1, null);

        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Authorizer.Calls);
        Assert.Equal(0, host.Service.Calls);
    }

    [Theory]
    [InlineData(IntakeDecisionStatus.Applied, HttpStatusCode.OK)]
    [InlineData(IntakeDecisionStatus.Conflict, HttpStatusCode.Conflict)]
    [InlineData(IntakeDecisionStatus.Invalid, HttpStatusCode.BadRequest)]
    [InlineData(IntakeDecisionStatus.NotFoundOrNotAuthorized, HttpStatusCode.NotFound)]
    public async Task ServiceOutcome_MapsToExpectedStatus(IntakeDecisionStatus status, HttpStatusCode expected)
    {
        await using var host = await TestDecisionHost.StartAsync(serviceStatus: status);
        var receiptId = Guid.NewGuid();
        using var request = DecisionRequest(receiptId, "Decline", 2, "Duplicate report.");

        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, host.Service.Calls);
        Assert.Equal(receiptId, host.Service.LastDecision!.ReceiptId);
        Assert.Equal(2, host.Service.LastDecision.ExpectedVersion);
        Assert.Equal(IntakeDecisionKind.Decline, host.Service.LastDecision.Decision);
        Assert.Equal("Duplicate report.", host.Service.LastDecision.ReviewReason);
        Assert.Equal("actor-1", host.Service.LastActor!.ActorId);
    }

    [Fact]
    public async Task AppliedDecision_ReturnsReceiptProjection()
    {
        await using var host = await TestDecisionHost.StartAsync(serviceStatus: IntakeDecisionStatus.Applied);
        using var request = DecisionRequest(Guid.NewGuid(), "RouteForReview", 1, null);

        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<IntakeReceipt>(body, JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(TestDecisionService.FixedReceiptId, receipt.ReceiptId);
        Assert.Equal(IntakeStatus.AwaitingReview, receipt.Status);
    }

    [Fact]
    public async Task PromotionUnavailable_MapsToServiceUnavailable()
    {
        await using var host = await TestDecisionHost.StartAsync(throwPromotionUnavailable: true);
        using var request = DecisionRequest(Guid.NewGuid(), "AcceptIntoCase", 1, null);

        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, host.Service.Calls);
    }

    [Fact]
    public void MapResult_CoversEveryDecisionStatus()
    {
        var receipt = new IntakeReceipt(Guid.NewGuid(), Guid.NewGuid(), IntakeStatus.Declined, IntakeRoutingStatus.RoutedForReview, DateTimeOffset.UtcNow);

        Assert.Equal(200, ((Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<IntakeReceipt>)IntakeDecisionEndpoints.MapResult(new(IntakeDecisionStatus.Applied, receipt))).StatusCode);
        Assert.Equal(404, ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)IntakeDecisionEndpoints.MapResult(new(IntakeDecisionStatus.NotFoundOrNotAuthorized, null))).StatusCode);
        Assert.Equal(409, ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)IntakeDecisionEndpoints.MapResult(new(IntakeDecisionStatus.Conflict, null))).StatusCode);
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)IntakeDecisionEndpoints.MapResult(new(IntakeDecisionStatus.Invalid, null))).StatusCode);
    }

    [Fact]
    public void MapRequest_RejectsMalformedInput()
    {
        var receiptId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => IntakeDecisionEndpoints.MapRequest(Guid.Empty, new(1, 1, "Decline", null)));
        Assert.Throws<ArgumentException>(() => IntakeDecisionEndpoints.MapRequest(receiptId, new(2, 1, "Decline", null)));
        Assert.Throws<ArgumentException>(() => IntakeDecisionEndpoints.MapRequest(receiptId, new(1, 0, "Decline", null)));
        Assert.Throws<ArgumentException>(() => IntakeDecisionEndpoints.MapRequest(receiptId, new(1, 1, "decline", null)));
        Assert.Throws<ArgumentException>(() => IntakeDecisionEndpoints.MapRequest(receiptId, new(1, 1, null, null)));
        Assert.Throws<ArgumentException>(() => IntakeDecisionEndpoints.MapRequest(receiptId, new(1, 1, "Decline", new string('x', 1001))));

        var decision = IntakeDecisionEndpoints.MapRequest(receiptId, new(1, 3, "RouteForReview", null));
        Assert.Equal(new IntakeDecision(receiptId, 3, IntakeDecisionKind.RouteForReview, null), decision);
    }

    private static HttpRequestMessage DecisionRequest(Guid receiptId, string decision, long version, string? reason) =>
        DecisionRequest(receiptId.ToString("D"), decision, version, reason);

    private static HttpRequestMessage DecisionRequest(string receiptId, string decision, long version, string? reason)
    {
        var payload = JsonSerializer.Serialize(
            new IntakeDecisionApiRequest(1, version, decision, reason), JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/intake/{receiptId}/decisions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        TestAuthenticationHandler.AddCookieAuthentication(request);
        return request;
    }
}

internal sealed class TestDecisionHost : IAsyncDisposable
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";
    private readonly WebApplication _application;
    private readonly IAntiforgery _antiforgery;
    private string? _antiforgeryCookie;

    private TestDecisionHost(
        WebApplication application,
        HttpClient client,
        TestDecisionService service,
        TestDecisionAuthorizer authorizer)
    {
        _application = application;
        Client = client;
        Service = service;
        Authorizer = authorizer;
        _antiforgery = application.Services.GetRequiredService<IAntiforgery>();
    }

    public HttpClient Client { get; }

    public TestDecisionService Service { get; }

    public TestDecisionAuthorizer Authorizer { get; }

    public static async Task<TestDecisionHost> StartAsync(
        bool authorized = true,
        IntakeDecisionStatus serviceStatus = IntakeDecisionStatus.Applied,
        bool throwPromotionUnavailable = false)
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

        var service = new TestDecisionService(serviceStatus, throwPromotionUnavailable);
        var authorizer = new TestDecisionAuthorizer(authorized);
        builder.Services.AddSingleton<IIntakeService>(service);
        builder.Services.AddSingleton<IIntakeReviewAuthorizer>(authorizer);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapIntakeDecisionEndpoints();
        await application.StartAsync().ConfigureAwait(false);

        return new TestDecisionHost(application, application.GetTestClient(), service, authorizer);
    }

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

internal sealed class TestDecisionService(
    IntakeDecisionStatus status = IntakeDecisionStatus.Applied,
    bool throwPromotionUnavailable = false) : IIntakeService
{
    public static readonly Guid FixedReceiptId = Guid.Parse("d2c8a3b4-1111-4d22-9aa9-123456789abc");

    public int Calls { get; private set; }

    public ActorContext? LastActor { get; private set; }

    public IntakeDecision? LastDecision { get; private set; }

    public Task<IntakeReceipt> SubmitAsync(IntakeSubmission command, IntakeCapability capability, CancellationToken ct) =>
        throw new NotSupportedException("The decision test service does not accept submissions.");

    public Task<IntakeDecisionResult> DecideAsync(IntakeDecision command, ActorContext actor, CancellationToken ct)
    {
        Calls++;
        LastActor = actor;
        LastDecision = command;
        if (throwPromotionUnavailable)
        {
            throw new IntakePromotionUnavailableException();
        }

        var receipt = status == IntakeDecisionStatus.Applied
            ? new IntakeReceipt(FixedReceiptId, Guid.NewGuid(), IntakeStatus.AwaitingReview, IntakeRoutingStatus.RoutedForReview, DateTimeOffset.UtcNow)
            : null;
        return Task.FromResult(new IntakeDecisionResult(status, receipt));
    }
}

internal sealed class TestDecisionAuthorizer(bool authorized = true) : IIntakeReviewAuthorizer
{
    private readonly bool _authorized = authorized;

    public int Calls { get; private set; }

    public Task<bool> IsAuthorizedAsync(ActorContext actor, Guid receiptId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_authorized);
    }
}
