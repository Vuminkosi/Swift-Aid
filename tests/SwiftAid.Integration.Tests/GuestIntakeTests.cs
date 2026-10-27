using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class GuestIntakeTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public void MappedRoutes_AreAnonymousWithBoundedBodies()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapPublicIntakeEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint =>
                string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                    ?? Enumerable.Empty<string>())
                + $" {endpoint.RoutePattern.RawText}");

        Assert.Equal(5, routes.Count);
        AssertRoute(routes, "POST", "/public/v1/intake/sessions", 64 * 1024);
        AssertRoute(routes, "POST", "/public/v1/intake/reports", 64 * 1024);
        AssertRoute(routes, "POST", "/public/v1/intake/receipt", 4096);
        AssertRoute(routes, "GET", "/public/v1/intake/metadata", null);
        AssertRoute(routes, "GET", "/public/v1/intake/metadata/countries", null);

        static void AssertRoute(
            Dictionary<string, RouteEndpoint> endpoints, string method, string path, long? maxBody)
        {
            var endpoint = endpoints[$"{method} {path}"];
            Assert.Null(endpoint.Metadata.GetMetadata<IAuthorizeData>());
            Assert.Equal(maxBody, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        }
    }

    [Fact]
    public async Task Session_MalformedJson_ReturnsBadRequestWithoutCallingService()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/public/v1/intake/sessions")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Sessions.CreateCalls);
    }

    [Fact]
    public async Task Session_InvalidProof_ReturnsBadRequest()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        using var request = SessionRequest(Guid.NewGuid(), "not-a-valid-proof!!!");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Sessions.CreateCalls);
    }

    [Fact]
    public async Task Session_OversizedChunkedBody_Returns413BeforeParsing()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/public/v1/intake/sessions")
        {
            Content = new UnknownLengthContent(new byte[(64 * 1024) + 1])
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Sessions.CreateCalls);
    }

    [Fact]
    public async Task Session_ValidRequest_DelegatesToServiceWithNoStore()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        var sessionId = Guid.NewGuid();
        using var request = SessionRequest(sessionId, NewProof());
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(1, host.Sessions.CreateCalls);
        Assert.Equal(sessionId, host.Sessions.LastSessionId);
    }

    [Fact]
    public async Task Submit_MissingHeaders_ReturnsBadRequestWithoutCallingServices()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/public/v1/intake/reports")
        {
            Content = SubmissionContent(ValidSubmission())
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Submissions.SubmitCalls);
        Assert.Equal(0, host.Sessions.ResolveCalls);
    }

    [Fact]
    public async Task Submit_MalformedJson_ReturnsBadRequest()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        using var request = AuthorizedSubmit("{", Guid.NewGuid(), NewProof());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Submissions.SubmitCalls);
    }

    [Fact]
    public async Task Submit_PrivilegedField_ReturnsBadRequestWithoutCallingServices()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        var node = JsonNode.Parse(JsonSerializer.Serialize(ValidSubmission(), JsonOptions))!;
        node["assignedStaffId"] = "mallory";
        using var request = AuthorizedSubmit(node.ToJsonString(), Guid.NewGuid(), NewProof());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Sessions.ResolveCalls);
        Assert.Equal(0, host.Submissions.SubmitCalls);
    }

    [Fact]
    public async Task Submit_GuessedProof_Returns401WithProofCode()
    {
        await using var host = await GuestIntakeHost.StartAsync(denyCapability: true);
        using var request = AuthorizedSubmit(SubmissionJson(ValidSubmission()), Guid.NewGuid(), NewProof());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("INTAKE_PROOF_INVALID", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Submissions.SubmitCalls);
    }

    [Fact]
    public async Task Submit_ValidRequest_Returns202ReceiptWithNoStore()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        var submission = ValidSubmission();
        using var request = AuthorizedSubmit(SubmissionJson(submission), Guid.NewGuid(), NewProof());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var receipt = await response.Content.ReadFromJsonAsync<IntakeReceipt>(JsonOptions);
        Assert.NotNull(receipt);
        Assert.Equal(submission.ClientSubmissionId, receipt.ClientSubmissionId);
        Assert.Equal(1, host.Submissions.SubmitCalls);
    }

    [Fact]
    public async Task Receipt_MalformedProof_ReturnsBadRequest()
    {
        await using var host = await GuestIntakeHost.StartAsync();
        using var request = ReceiptRequest(Guid.NewGuid(), "short");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Sessions.ReceiptCalls);
    }

    [Fact]
    public async Task Receipt_Unknown_Returns404WithProofCode()
    {
        await using var host = await GuestIntakeHost.StartAsync(receiptNotFound: true);
        using var request = ReceiptRequest(Guid.NewGuid(), NewProof());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("INTAKE_PROOF_INVALID", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Receipt_Found_Returns200WithReceipt()
    {
        var receipt = new IntakeReceipt(
            Guid.NewGuid(), Guid.NewGuid(), IntakeStatus.ReceivedUnverified,
            IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);
        await using var host = await GuestIntakeHost.StartAsync(findReceipt: receipt);
        using var request = ReceiptRequest(receipt.ReceiptId, NewProof());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IntakeReceipt>(JsonOptions);
        Assert.Equal(receipt.ReceiptId, body?.ReceiptId);
    }

    [Fact]
    public async Task Metadata_WithoutProvider_Returns503()
    {
        await using var host = await GuestIntakeHost.StartAsync(includeMetadata: false);

        using var response = await host.Client.GetAsync("/public/v1/intake/metadata");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_WithProvider_Returns200WithCacheHeader()
    {
        await using var host = await GuestIntakeHost.StartAsync();

        using var response = await host.Client.GetAsync("/public/v1/intake/metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<PublicIntakeMetadata>(JsonOptions);
        Assert.Equal("2026.10.27-1", body?.Version);
    }

    [Fact]
    public async Task Countries_WhenProviderThrows_Returns503()
    {
        await using var host = await GuestIntakeHost.StartAsync(throwCountries: true);

        using var response = await host.Client.GetAsync("/public/v1/intake/metadata/countries");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static HttpRequestMessage SessionRequest(Guid sessionId, string proof)
    {
        var body = JsonSerializer.Serialize(
            new CreateIntakeSessionRequest(sessionId, proof), JsonOptions);
        return new HttpRequestMessage(HttpMethod.Post, "/public/v1/intake/sessions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static HttpRequestMessage AuthorizedSubmit(string body, Guid sessionId, string proof)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/public/v1/intake/reports")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-SwiftAid-Intake-Capability", "opaque-capability");
        request.Headers.TryAddWithoutValidation("X-SwiftAid-Intake-Session", sessionId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-SwiftAid-Receipt-Proof", proof);
        return request;
    }

    private static HttpRequestMessage ReceiptRequest(Guid receiptId, string proof)
    {
        var body = JsonSerializer.Serialize(new IntakeReceiptQuery(receiptId), JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, "/public/v1/intake/receipt")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-SwiftAid-Receipt-Proof", proof);
        return request;
    }

    private static string SubmissionJson(IntakeSubmission submission) =>
        JsonSerializer.Serialize(submission, JsonOptions);

    private static StringContent SubmissionContent(IntakeSubmission submission) =>
        new(SubmissionJson(submission), Encoding.UTF8, "application/json");

    private static string NewProof() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IntakeCapability DefaultCapability() => new(
        Guid.NewGuid(), Guid.NewGuid(), new byte[32], DateTimeOffset.UtcNow.AddMinutes(30), 10);

    private static IntakeReceipt DefaultReceipt() => new(
        Guid.NewGuid(), Guid.NewGuid(), IntakeStatus.ReceivedUnverified,
        IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);

    private static IntakeSubmission ValidSubmission() => new(
        1,
        Guid.NewGuid(),
        IntakeSubjectScope.Human,
        "Saw a sick goat near the river.",
        ["Coughing"],
        [],
        new ReportedTime(DateTimeOffset.UtcNow, null, null, null, "+02:00", null, ReportedTimePrecision.Approximate),
        new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Unknown),
        new LocationContext(
            "ZA",
            new OptionalPlace("Western Cape", null, PresenceState.Known),
            new OptionalPlace("Cape Winelands", null, PresenceState.Known),
            new OptionalPlace("Stellenbosch", null, PresenceState.Known),
            null,
            null,
            null,
            LocationSource.Reporter,
            LocationPrecision.Approximate,
            "2026.10.27-1"),
        new IntakeConfirmations(
            true, "ZA", "2026.10.27-1", true, true,
            "https://swiftaid.online/privacy", "2026.10.27-1", null, null, null));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class GuestIntakeHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private GuestIntakeHost(WebApplication application, HttpClient client)
        {
            _application = application;
            Client = client;
            Sessions = (FakeSessionEndpointService)application.Services.GetRequiredService<IIntakeSessionEndpointService>();
            Submissions = (FakeIntakeService)application.Services.GetRequiredService<IIntakeService>();
        }

        public HttpClient Client { get; }

        public FakeSessionEndpointService Sessions { get; }

        public FakeIntakeService Submissions { get; }

        public static async Task<GuestIntakeHost> StartAsync(
            bool includeMetadata = true,
            bool throwCountries = false,
            IntakeReceipt? findReceipt = null,
            bool receiptNotFound = false,
            IntakeCapability? resolveCapability = null,
            bool denyCapability = false)
        {
            var sessions = new FakeSessionEndpointService(
                denyCapability ? null : resolveCapability ?? DefaultCapability(),
                receiptNotFound ? null : findReceipt ?? DefaultReceipt());
            var submissions = new FakeIntakeService();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IIntakeSessionEndpointService>(sessions);
            builder.Services.AddSingleton<IIntakeService>(submissions);
            if (includeMetadata)
            {
                builder.Services.AddSingleton<IIntakePublicMetadataProvider>(
                    new FakeMetadataProvider(throwCountries));
            }

            var application = builder.Build();
            application.MapPublicIntakeEndpoints();
            await application.StartAsync().ConfigureAwait(false);
            return new GuestIntakeHost(application, application.GetTestClient());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync().ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class FakeSessionEndpointService(
        IntakeCapability? capability,
        IntakeReceipt? receipt) : IIntakeSessionEndpointService
    {
        public int CreateCalls { get; private set; }

        public int ResolveCalls { get; private set; }

        public int ReceiptCalls { get; private set; }

        public Guid LastSessionId { get; private set; }

        public Task<IntakeSessionResponse?> CreateAsync(Guid sessionId, ReadOnlyMemory<byte> proof, CancellationToken ct)
        {
            CreateCalls++;
            LastSessionId = sessionId;
            return Task.FromResult<IntakeSessionResponse?>(
                new IntakeSessionResponse("opaque-capability", DateTimeOffset.UtcNow.AddMinutes(30)));
        }

        public Task<IntakeCapability?> ResolveCapabilityAsync(
            string opaqueCapability, Guid sessionId, ReadOnlyMemory<byte> proof, CancellationToken ct)
        {
            ResolveCalls++;
            return Task.FromResult(capability);
        }

        public Task<IntakeReceipt?> GetReceiptAsync(Guid receiptId, ReadOnlyMemory<byte> proof, CancellationToken ct)
        {
            ReceiptCalls++;
            return Task.FromResult(receipt);
        }
    }

    private sealed class FakeIntakeService : IIntakeService
    {
        public int SubmitCalls { get; private set; }

        public Task<IntakeReceipt> SubmitAsync(
            IntakeSubmission command, IntakeCapability capability, CancellationToken ct)
        {
            SubmitCalls++;
            return Task.FromResult(new IntakeReceipt(
                Guid.NewGuid(),
                command.ClientSubmissionId,
                IntakeStatus.ReceivedUnverified,
                IntakeRoutingStatus.NeedsRouting,
                DateTimeOffset.UtcNow));
        }

        public Task<IntakeDecisionResult> DecideAsync(
            IntakeDecision command, ActorContext actor, CancellationToken ct) =>
            Task.FromResult(new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null));
    }

    private sealed class FakeMetadataProvider(bool throwCountries) : IIntakePublicMetadataProvider
    {
        public PublicIntakeMetadata GetMetadata() => new(
            "2026.10.27-1", "2026.10.27-1", "/public/v1/intake/metadata/countries",
            "2026.10.27-1", "/privacy");

        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) =>
            throwCountries
                ? throw new InvalidOperationException("Catalogue unavailable.")
                : Task.FromResult(new PublicIntakeCountryCatalogue(
                    "2026.10.27-1", "/public/v1/intake/metadata/countries", []));
    }
}
