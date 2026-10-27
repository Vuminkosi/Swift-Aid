using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Contracts.Events;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class ManagedCaseRegistrationEndpointTests
{
    [Fact]
    public void MappedRoute_RequiresAuthorizationAndCapsRequestBodyBeforeBinding()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<IManagedCaseRegistrationProcessor, TestRegistrationProcessor>();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapManagedCaseRegistrationEndpoints();
        var routeBuilder = (IEndpointRouteBuilder)app;

        var endpoint = Assert.Single(
            routeBuilder.DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/api/v1/cases");

        var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
            authorization.AuthenticationSchemes);
        Assert.Equal(64L * 1024, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    [Fact]
    public async Task AnonymousMalformedRequest_IsRejectedBeforeBodyParsingOrProcessorCall()
    {
        await using var host = await TestEndpointHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task OversizedRequest_IsRejectedBeforeJsonParsing()
    {
        await using var host = await TestEndpointHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases")
        {
            Content = new ByteArrayContent(new byte[(64 * 1024) + 1])
        };
        TestAuthenticationHandler.AddCookieAuthentication(request);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task ChunkedOversizedRequest_IsRejectedByBoundedReader()
    {
        await using var host = await TestEndpointHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases")
        {
            Content = new UnknownLengthContent(new byte[(64 * 1024) + 1])
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task AuthenticatedCookieRequest_RequiresAntiforgeryBeforeCallingProcessor()
    {
        await using var host = await TestEndpointHost.StartAsync();
        using var request = CreateRegistrationRequest();

        using var response = await host.SendAuthenticatedAsync(request, antiforgery: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task MixedCookieAndBearerRequest_RequiresAntiforgeryBeforeCallingProcessor()
    {
        await using var host = await TestEndpointHost.StartAsync();
        using var request = CreateRegistrationRequest();
        TestAuthenticationHandler.AddCookieAuthentication(request);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestAuthenticationHandler.BearerToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Processor.Calls);
    }

    [Fact]
    public async Task ValidBearerRequest_DoesNotRequireAntiforgeryToken()
    {
        await using var host = await TestEndpointHost.StartAsync();
        using var request = CreateRegistrationRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, host.Processor.Calls);
        Assert.Equal("actor-1", host.Processor.LastActor?.ActorId);
    }

    [Fact]
    public async Task ReplayedReceipt_ReturnsTheProcessorReceiptWithoutReplacingIdentifiers()
    {
        await using var host = await TestEndpointHost.StartAsync();
        host.Processor.Status = CaseRegistrationStatus.Replayed;
        using var request = CreateRegistrationRequest();
        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<ManagedCaseRegistrationResponse>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(host.Processor.AcceptedCaseId, result.CaseId);
        Assert.Equal(host.Processor.AcceptedEventIds.ToArray(), result.EventIds.ToArray());
    }

    [Fact]
    public async Task ValidScopedPrincipal_RegistersAndReturnsOnlySafeReceipt()
    {
        await using var host = await TestEndpointHost.StartAsync();
        var commandId = Guid.NewGuid();
        using var request = CreateRegistrationRequest(commandId);
        using var response = await host.SendAuthenticatedAsync(request, await host.GetAntiforgeryAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ManagedCaseRegistrationResponse>(body, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(commandId, result.CommandId);
        Assert.Equal(host.Processor.AcceptedCaseId, result.CaseId);
        Assert.Equal(2, result.AggregateVersion);
        Assert.Equal(1, result.ReportRevision);
        Assert.Equal("actor-1", host.Processor.LastActor?.ActorId);
        Assert.Equal(7, host.Processor.LastActor?.AccessEpoch);
        Assert.Equal(commandId, host.Processor.LastRequest?.CommandId);
        Assert.DoesNotContain("actorId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grantGroup", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, host.Processor.Calls);
    }

    [Fact]
    public void MapRequest_HumanReport_PreservesUnknownFactsAndConfirmations()
    {
        var report = CreateHumanReport();
        var request = new ManagedCaseRegistrationApiRequest(1, Guid.NewGuid(), "workspace-1", report);

        var result = ManagedCaseRegistrationEndpoints.MapRequest(request);

        Assert.Equal(request.CommandId, result.CommandId);
        Assert.Equal("workspace-1", result.WorkspaceId);
        var human = Assert.IsType<CaseReportEventData.HumanV2>(result.Report);
        Assert.Equal(ReportedValueState.Unknown, human.Report.AgeYears.Value.State);
        Assert.Equal("worker-confirmed", human.Report.AgeYears.Provenance.StableReference);
        Assert.True(human.Confirmations.ReportReviewConfirmed);
    }

    [Fact]
    public void MapRequest_AnimalReport_PreservesKnownAndUnknownCounts()
    {
        var request = new ManagedCaseRegistrationApiRequest(1, Guid.NewGuid(), "workspace-1", CreateAnimalReport());

        var result = ManagedCaseRegistrationEndpoints.MapRequest(request);

        var animal = Assert.IsType<CaseReportEventData.AnimalV2>(result.Report);
        Assert.Equal(ReportedValueState.Unknown, animal.Report.AnimalsAtRisk.Value.State);
        Assert.Equal(ReportedValueState.Known, animal.Report.AnimalsAffected.Value.State);
        Assert.Equal(4, animal.Report.AnimalsAffected.Value.Value);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void MapRequest_UnsupportedSchemaVersion_IsRejected(int schemaVersion)
    {
        var request = new ManagedCaseRegistrationApiRequest(schemaVersion, Guid.NewGuid(), "workspace-1", CreateHumanReport());

        Assert.Throws<ArgumentException>(() => ManagedCaseRegistrationEndpoints.MapRequest(request));
    }

    [Fact]
    public void MapRequest_RejectsReportKindWithMismatchedSubjectSection()
    {
        var valid = CreateHumanReport();
        var invalid = valid with { Animal = new AnimalReportV2Payload(
            AnimalSubjectV2Scope.Herd,
            UnknownTextFact(),
            UnknownTextFact(),
            UnknownCount(),
            UnknownCount(),
            UnknownCount()) };
        var request = new ManagedCaseRegistrationApiRequest(1, Guid.NewGuid(), "workspace-1", invalid);

        Assert.Throws<ArgumentException>(() => ManagedCaseRegistrationEndpoints.MapRequest(request));
    }

    [Theory]
    [InlineData(CaseRegistrationStatus.Denied, 403)]
    [InlineData(CaseRegistrationStatus.Conflict, 409)]
    [InlineData(CaseRegistrationStatus.Invalid, 400)]
    [InlineData(CaseRegistrationStatus.Unavailable, 503)]
    public void MapResult_NonacceptedOutcomes_ReturnSafeProblemWithoutCaseIdentifier(
        CaseRegistrationStatus status,
        int expectedStatusCode)
    {
        var result = new CaseRegistrationResult(
            status,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [Guid.NewGuid()],
            2,
            1);

        var httpResult = ManagedCaseRegistrationEndpoints.MapResult(result);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult);
        Assert.Equal(expectedStatusCode, statusResult.StatusCode);
        var problemResult = Assert.IsType<ProblemHttpResult>(httpResult);
        var serialized = JsonSerializer.Serialize(problemResult.ProblemDetails);
        Assert.False(serialized.Contains(result.CaseId!.Value.ToString(), StringComparison.Ordinal));
        Assert.False(serialized.Contains(result.CommandId.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CaseRegistrationStatus.Accepted, 201)]
    [InlineData(CaseRegistrationStatus.Replayed, 200)]
    public void MapResult_AcceptedOrReplayedOutcome_ReturnsAuthoritativeReceipt(
        CaseRegistrationStatus status,
        int expectedStatusCode)
    {
        var caseId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var eventIds = ImmutableArray.Create(Guid.NewGuid(), Guid.NewGuid());
        var result = new CaseRegistrationResult(status, caseId, commandId, eventIds, 2, 1);

        var httpResult = ManagedCaseRegistrationEndpoints.MapResult(result);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult);
        Assert.Equal(expectedStatusCode, statusResult.StatusCode);
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(httpResult);
        var response = Assert.IsType<ManagedCaseRegistrationResponse>(valueResult.Value);
        Assert.Equal(caseId, response.CaseId);
        Assert.Equal(commandId, response.CommandId);
        Assert.Equal(eventIds, response.EventIds);
        Assert.Equal(2, response.AggregateVersion);
        Assert.Equal(1, response.ReportRevision);
    }

    [Fact]
    public void MapResult_MalformedAcceptedOutcome_FailsClosedWithoutIdentifiers()
    {
        var result = new CaseRegistrationResult(
            CaseRegistrationStatus.Accepted,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [Guid.NewGuid()],
            1,
            1);

        var httpResult = ManagedCaseRegistrationEndpoints.MapResult(result);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);
        Assert.IsType<ProblemHttpResult>(httpResult);
    }

    [Fact]
    public void ApiRequest_RejectsCallerSelectedCaseActorOrOwnerFields()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "commandId": "8f14e45f-ea9b-4e9e-ae2d-40d6442f5d20",
              "workspaceId": "workspace-1",
              "report": null,
              "caseId": "4e53bbd2-2523-4e2f-876c-c320b428dbbf",
              "actorId": "caller-selected",
              "ownerId": "caller-selected"
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ManagedCaseRegistrationApiRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static CaseReportV2Payload CreateHumanReport() => new(
        "Human",
        Guid.NewGuid(),
        CreateContext(),
        CreateEvidence(),
        CreateConfirmations(),
        new HumanReportV2Payload(UnknownTextFact(), UnknownIntFact(), KnownTextFact("reported concern")),
        null);

    private static CaseReportV2Payload CreateAnimalReport() => new(
        "Animal",
        Guid.NewGuid(),
        CreateContext(),
        CreateEvidence(),
        CreateConfirmations(),
        null,
        new AnimalReportV2Payload(
            AnimalSubjectV2Scope.Herd,
            KnownTextFact("cattle"),
            UnknownTextFact(),
            UnknownCount(),
            KnownCount(4),
            UnknownCount()));

    private static ReportContextV2Payload CreateContext() => new(
        new ReportedFactV2Payload<ReportedTime>(
            ReportedValueV2State.Unknown,
            null,
            Provenance(ReportSourceV2Kind.WorkerConfirmed, "worker-confirmed")),
        null,
        null,
        null,
        ReportedTimePrecision.Unknown,
        new LocationContext(
            "SZ",
            new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown),
            null,
            null,
            null,
            LocationSource.Reporter,
            LocationPrecision.Unknown,
            "catalogue-v1"));

    private static ReportEvidenceV2Payload CreateEvidence() => new(
        new ReportedTextV2Payload("Reported concern", Provenance(ReportSourceV2Kind.ReporterNarrative, "narrative-1")),
        Array.Empty<ReportedTextV2Payload>(),
        null);

    private static ReportConfirmationsV2Payload CreateConfirmations() => new(
        true,
        "SZ",
        "catalogue-v1",
        true,
        true,
        "processing-policy",
        "v1",
        null,
        null,
        null);

    private static ReportedFactV2Payload<string> UnknownTextFact() => new(
        ReportedValueV2State.Unknown,
        null,
        Provenance(ReportSourceV2Kind.WorkerConfirmed, "worker-confirmed"));

    private static ReportedFactV2Payload<string> KnownTextFact(string value) => new(
        ReportedValueV2State.Known,
        JsonSerializer.SerializeToElement(value),
        Provenance(ReportSourceV2Kind.ReporterNarrative, "narrative-1"));

    private static ReportedFactV2Payload<int> UnknownIntFact() => new(
        ReportedValueV2State.Unknown,
        null,
        Provenance(ReportSourceV2Kind.WorkerConfirmed, "worker-confirmed"));

    private static AnimalCountV2Payload UnknownCount() => new(
        ReportedValueV2State.Unknown,
        null,
        Provenance(ReportSourceV2Kind.WorkerConfirmed, "worker-confirmed"),
        null,
        null);

    private static AnimalCountV2Payload KnownCount(int count) => new(
        ReportedValueV2State.Known,
        count,
        Provenance(ReportSourceV2Kind.WorkerConfirmed, "worker-confirmed"),
        "population-1",
        null);

    private static ReportProvenanceV2Payload Provenance(ReportSourceV2Kind source, string reference) => new(source, reference);

    private static HttpRequestMessage CreateRegistrationRequest(Guid? commandId = null)
    {
        var request = new ManagedCaseRegistrationApiRequest(
            1, commandId ?? Guid.NewGuid(), "workspace-1", CreateHumanReport());
        return new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases")
        {
            Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed class TestEndpointHost : IAsyncDisposable
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";
    private readonly WebApplication _application;
    private readonly IAntiforgery _antiforgery;
    private string? _antiforgeryCookie;

    private TestEndpointHost(WebApplication application, HttpClient client, TestRegistrationProcessor processor)
    {
        _application = application;
        Client = client;
        Processor = processor;
        _antiforgery = application.Services.GetRequiredService<IAntiforgery>();
    }

    public HttpClient Client { get; }

    public TestRegistrationProcessor Processor { get; }

    public static async Task<TestEndpointHost> StartAsync()
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

        var processor = new TestRegistrationProcessor();
        builder.Services.AddSingleton<IManagedCaseRegistrationProcessor>(processor);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapManagedCaseRegistrationEndpoints();
        await application.StartAsync().ConfigureAwait(false);

        return new TestEndpointHost(application, application.GetTestClient(), processor);
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

internal sealed class TestRegistrationProcessor : IManagedCaseRegistrationProcessor
{
    public int Calls { get; private set; }

    public Guid AcceptedCaseId { get; } = Guid.NewGuid();

    public ImmutableArray<Guid> AcceptedEventIds { get; } = [Guid.NewGuid(), Guid.NewGuid()];

    public CaseRegistrationStatus Status { get; set; } = CaseRegistrationStatus.Accepted;

    public ActorContext? LastActor { get; private set; }

    public RegisterManagedCaseRequest? LastRequest { get; private set; }

    public ValueTask<CaseRegistrationResult> RegisterAsync(
        ActorContext actor,
        RegisterManagedCaseRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastActor = actor;
        LastRequest = request;
        return ValueTask.FromResult(new CaseRegistrationResult(
            Status,
            AcceptedCaseId,
            request.CommandId,
            AcceptedEventIds,
            2,
            1));
    }
}

internal sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string CookieName = "swiftaid-test-auth";
    public const string CookieValue = "authenticated";
    public const string BearerToken = "test-access-token";

    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var isCookie = string.Equals(Scheme.Name, IdentityConstants.ApplicationScheme, StringComparison.Ordinal);
        var isBearer = string.Equals(
            Scheme.Name,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            StringComparison.Ordinal);
        var authenticated = false;
        if (isCookie)
        {
            authenticated = Request.Cookies.TryGetValue(CookieName, out var cookie) &&
                string.Equals(cookie, CookieValue, StringComparison.Ordinal);
        }
        else if (isBearer)
        {
            authenticated = string.Equals(
                Request.Headers["Authorization"].ToString(),
                $"Bearer {BearerToken}",
                StringComparison.Ordinal);
        }

        if (!authenticated)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = CreatePrincipal(Scheme.Name);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    public static ClaimsPrincipal CreatePrincipal(string authenticationType)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "actor-1"),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ],
        authenticationType);
        return new ClaimsPrincipal(identity);
    }

    public static void AddCookieAuthentication(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("Cookie", $"{CookieName}={CookieValue}");
}

internal sealed class UnknownLengthContent(byte[] content) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
        stream.WriteAsync(content, 0, content.Length);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
