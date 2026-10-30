using System.Security.Claims;
using System.Text;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiPrivilegedRouteAuthorizationTests
{
    [Fact]
    public async Task MixedCaseSessionStartIsDeniedWithoutCreatingSession()
    {
        await using var app = await StartApplicationAsync();
        var request = new StartSessionRequest(EllaiMode.Ask, "start-request", null, null, null, null);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/API/V1/ELLAI/SESSIONS", request);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, app.Services.GetRequiredService<CountingEllaiSessionStore>().CreatedCount);
    }

    [Theory]
    [InlineData("/API/V1/ELLAI/SESSIONS/session-1/APPROVAL-REVIEWS/ACTIVE")]
    [InlineData("/api/v1/ellai/sessions/session-1/ReCeIpTs")]
    public async Task MixedCasePrivilegedGetIsDeniedBeforeHandlerRuns(string path)
    {
        await using var app = await StartApplicationAsync();
        var response = await app.GetTestClient().GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MixedCaseReviewRejectIsDeniedWithoutChangingReviewState()
    {
        await using var app = await StartApplicationAsync();
        var client = app.GetTestClient();
        using var body = new StringContent("{\"sessionId\":\"session-1\"}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync(
            "/api/v1/ellai/sessions/session-1/approval-reviews/review-1/ReJeCt", body);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var review = app.Services.GetRequiredService<EllaiApprovalDialogCoordinator>()
            .Get("review-1");
        Assert.Equal(EllaiApprovalDialogState.AwaitingApproval, review!.State);
    }

    [Fact]
    public async Task MixedCaseUnknownUtteranceIsPolicyBlockedWithoutMutationOrQueueing()
    {
        await using var app = await StartApplicationAsync();
        var request = new SubmitUtteranceRequest(
            "utterance-1", "session-1", 1,
            "patient case 4812 narrative", null, "typed", 1);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/API/V1/ELLAI/SESSIONS/session-1/UTTERANCES", request);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var session = await app.Services.GetRequiredService<IEllaiSessionStore>()
            .GetAsync("session-1", CancellationToken.None);
        Assert.Equal(1, session!.SessionVersion);
        Assert.Equal(0, session.CyclesUsed);
        Assert.Equal(0, app.Services.GetRequiredService<EllaiUtteranceQueue>().PendingCount);
    }

    [Fact]
    public async Task ForeignOwnerCannotSubmitUtteranceOrMutateSession()
    {
        await using var app = await StartApplicationAsync(sessionOwnerId: "another-owner");
        var request = new SubmitUtteranceRequest(
            "utterance-foreign", "session-1", 1,
            "sensitive case narrative", null, "typed", 1);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/api/v1/ellai/sessions/session-1/utterances", request);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var session = await app.Services.GetRequiredService<IEllaiSessionStore>()
            .GetAsync("session-1", CancellationToken.None);
        Assert.Equal(1, session!.SessionVersion);
        Assert.Equal(0, session.CyclesUsed);
        Assert.Equal(0, app.Services.GetRequiredService<EllaiUtteranceQueue>().PendingCount);
    }

    [Fact]
    public async Task HttpAndInProcessCancellationBothDenyForeignOwnerWithoutLeakingSession()
    {
        await using var app = await StartApplicationAsync(sessionOwnerId: "another-owner");
        var httpClient = new EllaiReviewHttpClient(app.GetTestClient());
        var httpResult = await httpClient.CancelSessionAsync("session-1", 1, CancellationToken.None);
        var inProcess = new EllaiReviewGatewayClient(
            new FixedAuthenticationStateProvider(TestPrincipal()), app.Services);
        var inProcessResult = await inProcess.CancelSessionAsync("session-1", 1, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Unauthorized, httpResult.Status);
        Assert.Equal(EllaiClientOperationStatus.Unauthorized, inProcessResult.Status);
        Assert.Null(httpResult.Session);
        Assert.Null(inProcessResult.Session);
        Assert.Equal("Active", (await app.Services.GetRequiredService<IEllaiSessionStore>()
            .GetAsync("session-1", CancellationToken.None))!.Status);
        Assert.Empty(app.Services.GetRequiredService<EllaiSessionEventJournal>()
            .ReadSince("session-1", 0).Events);
        Assert.Equal(EllaiApprovalDialogState.AwaitingApproval,
            app.Services.GetRequiredService<EllaiApprovalDialogCoordinator>().Get("review-1")!.State);
    }

    [Fact]
    public async Task HttpAndInProcessCancellationShareStaleSuccessAndReplayOutcomes()
    {
        await using var app = await StartApplicationAsync();
        using var http = app.GetTestClient();
        var httpClient = new EllaiReviewHttpClient(http);
        var inProcess = new EllaiReviewGatewayClient(
            new FixedAuthenticationStateProvider(TestPrincipal()), app.Services);

        var httpStale = await httpClient.CancelSessionAsync("session-1", 0, CancellationToken.None);
        var inProcessStale = await inProcess.CancelSessionAsync("session-1", 0, CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.Conflict, httpStale.Status);
        Assert.Equal(EllaiClientOperationStatus.Conflict, inProcessStale.Status);
        Assert.Equal(1, httpStale.Session!.SessionVersion);
        Assert.Equal(1, inProcessStale.Session!.SessionVersion);
        Assert.Empty(app.Services.GetRequiredService<EllaiSessionEventJournal>()
            .ReadSince("session-1", 0).Events);

        var httpCancelled = await httpClient.CancelSessionAsync("session-1", 1, CancellationToken.None);
        var replayCancelled = await inProcess.CancelSessionAsync("session-1", 1, CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.Cancelled, httpCancelled.Status);
        Assert.Equal(EllaiClientOperationStatus.Cancelled, replayCancelled.Status);
        Assert.Equal(EllaiApprovalDialogState.Cancelled,
            app.Services.GetRequiredService<EllaiApprovalDialogCoordinator>().Get("review-1")!.State);
        var events = app.Services.GetRequiredService<EllaiSessionEventJournal>()
            .ReadSince("session-1", 0).Events;
        Assert.Equal("session-cancelled", Assert.Single(events).Kind);
    }

    [Fact]
    public async Task HttpAndInProcessCancellationCannotChangeCompletedSession()
    {
        await using var app = await StartApplicationAsync(sessionStatus: "Completed");
        using var http = app.GetTestClient();
        var httpClient = new EllaiReviewHttpClient(http);
        var inProcess = new EllaiReviewGatewayClient(
            new FixedAuthenticationStateProvider(TestPrincipal()), app.Services);

        var httpResult = await httpClient.CancelSessionAsync("session-1", 1, CancellationToken.None);
        var inProcessResult = await inProcess.CancelSessionAsync("session-1", 1, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Conflict, httpResult.Status);
        Assert.Equal(EllaiClientOperationStatus.Conflict, inProcessResult.Status);
        Assert.Equal("Completed", httpResult.Session!.Status);
        Assert.Equal("Completed", inProcessResult.Session!.Status);
        Assert.Empty(app.Services.GetRequiredService<EllaiSessionEventJournal>()
            .ReadSince("session-1", 0).Events);
        Assert.Equal(EllaiApprovalDialogState.AwaitingApproval,
            app.Services.GetRequiredService<EllaiApprovalDialogCoordinator>().Get("review-1")!.State);
    }

    private static async Task<WebApplication> StartApplicationAsync(
        string sessionOwnerId = "test-owner", string sessionStatus = "Active")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, EllaiTestAuthenticationHandler>(
                IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, EllaiTestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("SwiftAidStaffApi", policy =>
        {
            policy.AddAuthenticationSchemes(
                IdentityConstants.ApplicationScheme,
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
        }));

        var sessionStore = new CountingEllaiSessionStore();
        await sessionStore.CreateAsync(new EllaiSessionRecord(
            "session-1", EllaiMode.Ask, sessionStatus, 1, 1, 1, 1,
            0, EllaiSessionService.DefaultCyclesAllowed, sessionOwnerId, "scope-test",
            null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        builder.Services.AddSingleton<IEllaiSessionStore>(sessionStore);
        builder.Services.AddSingleton(sessionStore);
        builder.Services.AddSingleton<EllaiConsentService>();
        builder.Services.AddSingleton<IEllaiConsentStore, InMemoryEllaiConsentStore>();
        builder.Services.AddSingleton<EllaiUtteranceQueue>();
        builder.Services.AddSingleton<EllaiUtteranceSubmissionService>();
        builder.Services.AddSingleton<EllaiSessionService>();
        builder.Services.AddSingleton<EllaiApprovedActionService>();
        builder.Services.AddSingleton<EllaiApprovalService>();
        builder.Services.AddSingleton<IEllaiUiObservationStore, InMemoryEllaiUiObservationStore>();
        builder.Services.AddSingleton<EllaiApprovalDialogCoordinator>(services =>
        {
            var dialogs = new EllaiApprovalDialogCoordinator();
            dialogs.Register("session-1", "review-1", "plan-hash", 1,
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            return dialogs;
        });
        builder.Services.AddSingleton<EllaiSessionEventJournal>();
        builder.Services.AddSingleton<IEllaiApprovalStore, EllaiReceiptStore>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEllaiEndpoints();
        await app.StartAsync();
        return app;
    }

    private static ClaimsPrincipal TestPrincipal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "test-owner"),
            new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
}

public sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(principal));
}

public sealed class CountingEllaiSessionStore : IEllaiSessionStore
{
    private readonly InMemoryEllaiSessionStore _inner = new();
    public int CreatedCount { get; private set; }

    public Task<EllaiSessionRecord?> GetAsync(string sessionId, CancellationToken ct) =>
        _inner.GetAsync(sessionId, ct);

    public Task<EllaiSessionRecord> CreateAsync(EllaiSessionRecord record, CancellationToken ct)
    {
        CreatedCount++;
        return _inner.CreateAsync(record, ct);
    }

    public Task<EllaiSessionRecord?> TryUpdateAsync(EllaiSessionRecord record, long expectedVersion, CancellationToken ct) =>
        _inner.TryUpdateAsync(record, expectedVersion, ct);

    public Task<EllaiUtteranceStoreResult?> TryAcceptUtteranceAsync(
        EllaiSessionRecord nextRecord, long expectedVersion, string clientUtteranceId, CancellationToken ct) =>
        _inner.TryAcceptUtteranceAsync(nextRecord, expectedVersion, clientUtteranceId, ct);

    public Task SaveCheckpointAsync(EllaiCheckpoint checkpoint, CancellationToken ct) =>
        _inner.SaveCheckpointAsync(checkpoint, ct);
}

public sealed class EllaiTestAuthenticationHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "test-owner"),
                new Claim(ActorContext.AccessEpochClaimType, "1")], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}
