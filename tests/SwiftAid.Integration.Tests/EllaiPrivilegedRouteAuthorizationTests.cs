using System.Security.Claims;
using System.Text;
using System.Net.Http.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task MixedCaseSessionStartCreatesIdentityOnlyHelpSessionWithoutWorkspaceScope()
    {
        await using var app = await StartApplicationAsync();
        var request = new StartSessionRequest(EllaiMode.Ask, "start-request", null, null, null, null);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/API/V1/ELLAI/SESSIONS", request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<EllaiSessionView>();
        Assert.NotNull(session);
        Assert.Equal("ellai-help-identity-epoch-1", session.ScopeDisplay);
        Assert.Equal(2, app.Services.GetRequiredService<CountingEllaiSessionStore>().CreatedCount);
    }

    [Fact]
    public async Task SessionStartUsesTheCurrentActorScopeAndPersistsItsAccessEpoch()
    {
        await using var app = await StartApplicationAsync(hasCurrentScope: true);
        var request = new StartSessionRequest(EllaiMode.Ask, "scoped-start", null, null, null, null);

        var response = await app.GetTestClient().PostAsJsonAsync("/api/v1/ellai/sessions", request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<EllaiSessionView>();
        Assert.NotNull(session);
        Assert.Equal("access-epoch-1", session.ScopeDisplay);
        Assert.Equal(2, app.Services.GetRequiredService<CountingEllaiSessionStore>().CreatedCount);
    }

    [Fact]
    public async Task InProcessSessionStartCreatesIdentityOnlyHelpSessionWithoutWorkspaceScope()
    {
        await using var app = await StartApplicationAsync();
        var client = new EllaiReviewGatewayClient(
            new FixedAuthenticationStateProvider(TestPrincipal()), app.Services);

        var result = await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        Assert.Equal("ellai-help-identity-epoch-1", result.Session!.ScopeDisplay);
        Assert.Equal(2, app.Services.GetRequiredService<CountingEllaiSessionStore>().CreatedCount);
    }

    [Fact]
    public async Task InProcessSessionStartUsesTheCurrentActorScope()
    {
        await using var app = await StartApplicationAsync(hasCurrentScope: true);
        var client = new EllaiReviewGatewayClient(
            new FixedAuthenticationStateProvider(TestPrincipal()), app.Services);

        var result = await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        Assert.Equal("access-epoch-1", result.Session!.ScopeDisplay);
        Assert.Equal(2, app.Services.GetRequiredService<CountingEllaiSessionStore>().CreatedCount);
    }

    [Fact]
    public async Task AnonymousInProcessClientCanUseOnlyScopedPublicHelp()
    {
        await using var app = await StartApplicationAsync();
        var guestIdentity = new EllaiGuestSessionIdentity();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var client = new EllaiReviewGatewayClient(
            new FixedAuthenticationStateProvider(anonymous), app.Services, guestIdentity);

        var deniedMode = await client.StartSessionAsync(EllaiMode.GuideMe, CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.Unauthorized, deniedMode.Status);

        var started = await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.Accepted, started.Status);
        Assert.StartsWith("ellai-help-identity-epoch-0", started.Session!.ScopeDisplay, StringComparison.Ordinal);
        var record = await app.Services.GetRequiredService<IEllaiSessionStore>()
            .GetAsync(started.Session.SessionId, CancellationToken.None);
        Assert.StartsWith("ellai-guest-", record!.OwnerActorId, StringComparison.Ordinal);

        Assert.True(await client.GrantPublicProductHelpConsentAsync(
            started.Session.SessionId, CancellationToken.None));
        var submitted = await client.SubmitUtteranceAsync(new SubmitUtteranceRequest(
            "server-guest-question", started.Session.SessionId, started.Session.SessionVersion,
            "What does an intake receipt mean?", null, "typed", 1), CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.Accepted, submitted.Status);
        Assert.False(await client.ReportUiObservationAsync(
            started.Session.SessionId,
            new UiObservationReport("view", "context", "web", ["public-home"]),
            CancellationToken.None));

        var outbox = await app.Services.GetRequiredService<IEllaiUtteranceOutboxStore>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.NotNull(outbox);
        Assert.DoesNotContain("What does an intake receipt mean?", outbox!.PayloadJson, StringComparison.OrdinalIgnoreCase);
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
    public async Task SessionResponseEndpointReturnsDataOnlyToTheSessionOwner()
    {
        await using var ownerApp = await StartApplicationAsync();
        var ownerStore = ownerApp.Services.GetRequiredService<EllaiSessionResponseStore>();
        ownerStore.Publish("session-1", "utterance-1", "Current turn output.");

        using var ownerResponse = await ownerApp.GetTestClient().GetAsync(
            "/api/v1/ellai/sessions/session-1/responses?cursor=0");

        Assert.Equal(System.Net.HttpStatusCode.OK, ownerResponse.StatusCode);
        var page = await ownerResponse.Content.ReadFromJsonAsync<EllaiSessionResponsePageDto>();
        Assert.Equal("Current turn output.", Assert.Single(page!.Responses).Text);

        await using var foreignApp = await StartApplicationAsync(sessionOwnerId: "different-owner");
        foreignApp.Services.GetRequiredService<EllaiSessionResponseStore>()
            .Publish("session-1", "utterance-foreign", "Must remain private.");
        using var foreignResponse = await foreignApp.GetTestClient().GetAsync(
            "/api/v1/ellai/sessions/session-1/responses?cursor=0");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, foreignResponse.StatusCode);
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
    public async Task PublicProductHelpConsentUsesOnlyServerAssignedSafeClassAndFixedPurpose()
    {
        await using var app = await StartApplicationAsync();
        using var client = app.GetTestClient();
        var accepted = await client.PostAsJsonAsync(
            "/api/v1/ellai/sessions/session-1/consents",
            new GrantEllaiConsentApiRequest("session-1", "ReasoningText",
                EllaiConsentPurposes.PublicProductHelp, 1));

        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
        var grant = await accepted.Content.ReadFromJsonAsync<EllaiConsentGrant>();
        Assert.NotNull(grant);
        Assert.Equal(EllaiProcessingClassification.ConsentedNonSensitive, grant.DataClass);
        Assert.Equal(EllaiConsentPurposes.PublicProductHelp, grant.Purpose);

        var arbitrary = await client.PostAsJsonAsync(
            "/api/v1/ellai/sessions/session-1/consents",
            new GrantEllaiConsentApiRequest("session-1", "ReasoningText", "caller-selected", 1));
        var broadDimension = await client.PostAsJsonAsync(
            "/api/v1/ellai/sessions/session-1/consents",
            new GrantEllaiConsentApiRequest("session-1", "Microphone",
                EllaiConsentPurposes.PublicProductHelp, 1));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, arbitrary.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, broadDimension.StatusCode);
        Assert.Single(await app.Services.GetRequiredService<IEllaiConsentStore>()
            .ListBySessionAsync("session-1", CancellationToken.None));

        var submitted = await client.PostAsJsonAsync(
            "/api/v1/ellai/sessions/session-1/utterances",
            new SubmitUtteranceRequest("public-question-1", "session-1", 1,
                "What does an intake receipt mean?", null, "typed", 1));
        Assert.Equal(System.Net.HttpStatusCode.OK, submitted.StatusCode);
        var acceptedSession = await submitted.Content.ReadFromJsonAsync<EllaiSessionView>();
        Assert.Equal(2, acceptedSession!.SessionVersion);
        var outbox = await app.Services.GetRequiredService<IEllaiUtteranceOutboxStore>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.NotNull(outbox);
        var work = EllaiUtteranceWorkEnvelope.Deserialize(outbox!.PayloadJson);
        Assert.DoesNotContain("What does an intake receipt mean?", work.Utterance, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([EllaiToolNames.ProductFacts], work.AllowedTools);
    }

    [Fact]
    public async Task GuestCanUseRateLimitedPublicHelpWithoutGettingWorkspaceTools()
    {
        await using var app = await StartApplicationAsync();
        using var client = app.GetTestClient();
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var antiforgeryResponse = await client.GetAsync("/api/v1/ellai/guest/antiforgery"))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, antiforgeryResponse.StatusCode);
            MergeCookies(cookies, antiforgeryResponse);
            var token = (await antiforgeryResponse.Content.ReadFromJsonAsync<EllaiGuestAntiforgeryToken>())!
                .RequestToken;
            using (var invalidMode = await SendGuestPostAsync(client, cookies, token,
                "/api/v1/ellai/guest/sessions",
                new StartSessionRequest(EllaiMode.GuideMe, "guest-start-invalid", null, null, null, null)))
            {
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidMode.StatusCode);
            }

            using var started = await SendGuestPostAsync(client, cookies, token,
                "/api/v1/ellai/guest/sessions",
                new StartSessionRequest(EllaiMode.Ask, "guest-start-1", null, null, null, null));
            Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);
            MergeCookies(cookies, started);
            var session = (await started.Content.ReadFromJsonAsync<EllaiSessionView>())!;
            Assert.StartsWith("ellai-help-identity-epoch-0", session.ScopeDisplay, StringComparison.Ordinal);

            using (var stateRequest = new HttpRequestMessage(HttpMethod.Get,
                       $"/api/v1/ellai/guest/sessions/{session.SessionId}/voice/state"))
            {
                stateRequest.Headers.TryAddWithoutValidation("Cookie",
                    string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
                using var stateResponse = await client.SendAsync(stateRequest);
                Assert.Equal(System.Net.HttpStatusCode.OK, stateResponse.StatusCode);
                var stateJson = await stateResponse.Content.ReadAsStringAsync();
                Assert.DoesNotContain("transcript", stateJson, StringComparison.OrdinalIgnoreCase);
                var voiceState = await stateResponse.Content.ReadFromJsonAsync<EllaiVoiceTurnStatusDto>();
                Assert.Equal("Idle", voiceState?.State);
            }

            var responseStore = app.Services.GetRequiredService<EllaiSessionResponseStore>();
            responseStore.Publish(session.SessionId, "guest-utterance-1", "Guest-safe current turn output.");
            using (var responseRequest = new HttpRequestMessage(HttpMethod.Get,
                       $"/api/v1/ellai/guest/sessions/{session.SessionId}/responses?cursor=0"))
            {
                responseRequest.Headers.TryAddWithoutValidation("Cookie",
                    string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
                using var guestResponse = await client.SendAsync(responseRequest);
                Assert.Equal(System.Net.HttpStatusCode.OK, guestResponse.StatusCode);
                var responsePage = await guestResponse.Content.ReadFromJsonAsync<EllaiSessionResponsePageDto>();
                Assert.Equal("Guest-safe current turn output.", Assert.Single(responsePage!.Responses).Text);
            }

            using var consent = await SendGuestPostAsync(client, cookies, token,
                $"/api/v1/ellai/guest/sessions/{session.SessionId}/consents",
                new GrantEllaiConsentApiRequest(session.SessionId, "ReasoningText",
                    EllaiConsentPurposes.PublicProductHelp, 1));
            Assert.Equal(System.Net.HttpStatusCode.OK, consent.StatusCode);

            using var submitted = await SendGuestPostAsync(client, cookies, token,
                $"/api/v1/ellai/guest/sessions/{session.SessionId}/utterances",
                new SubmitUtteranceRequest("guest-question-1", session.SessionId, session.SessionVersion,
                    "What does an intake receipt mean?", null, "typed", 1));
            Assert.Equal(System.Net.HttpStatusCode.OK, submitted.StatusCode);
            var accepted = (await submitted.Content.ReadFromJsonAsync<EllaiSessionView>())!;
            Assert.Equal(session.SessionVersion + 1, accepted.SessionVersion);

            var work = await app.Services.GetRequiredService<IEllaiUtteranceOutboxStore>()
                .ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
            Assert.NotNull(work);
            var envelope = EllaiUtteranceWorkEnvelope.Deserialize(work!.PayloadJson);
            Assert.StartsWith("ellai-guest-", envelope.ActorId, StringComparison.Ordinal);
            Assert.Equal([EllaiToolNames.ProductFacts], envelope.AllowedTools);
            Assert.DoesNotContain("What does an intake receipt mean?", envelope.Utterance, StringComparison.OrdinalIgnoreCase);

            using var sensitive = await SendGuestPostAsync(client, cookies, token,
                $"/api/v1/ellai/guest/sessions/{session.SessionId}/utterances",
                new SubmitUtteranceRequest("guest-question-sensitive", session.SessionId, accepted.SessionVersion,
                    "Patient has a fever, what should I do?", null, "typed", 2));
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, sensitive.StatusCode);
            var afterSensitive = await app.Services.GetRequiredService<IEllaiSessionStore>()
                .GetAsync(session.SessionId, CancellationToken.None);
            Assert.Equal(1, afterSensitive!.CyclesUsed);
        }

        using var unauthorizedPost = await client.PostAsJsonAsync(
            "/api/v1/ellai/guest/sessions/unknown/utterances",
            new SubmitUtteranceRequest("no-cookie", "unknown", 1, "help", null, "typed", 1));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, unauthorizedPost.StatusCode);

        static async Task<HttpResponseMessage> SendGuestPostAsync<T>(
            HttpClient client, Dictionary<string, string> cookies, string token, string path, T body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.TryAddWithoutValidation("Cookie",
                string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
            return await client.SendAsync(request);
        }

        static void MergeCookies(Dictionary<string, string> cookies, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                return;
            }

            foreach (var value in values)
            {
                var pair = value.Split(';', 2)[0];
                var equals = pair.IndexOf('=');
                if (equals > 0)
                {
                    cookies[pair[..equals]] = pair[(equals + 1)..];
                }
            }
        }
    }

    [Fact]
    public async Task AuthenticatedVoiceTurnStateIsSessionOwnerScopedAndTranscriptFree()
    {
        await using var app = await StartApplicationAsync();
        var coordinator = app.Services.GetRequiredService<EllaiVoiceTurnCoordinator>();
        using var stream = coordinator.BeginStream("session-1", CancellationToken.None);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/v1/ellai/sessions/session-1/voice/state");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("transcript", payload, StringComparison.OrdinalIgnoreCase);
        var status = await response.Content.ReadFromJsonAsync<EllaiVoiceTurnStatusDto>();
        Assert.Equal("Listening", status?.State);
        Assert.Equal(stream.Epoch, status?.Epoch);

        await using var otherOwnerApp = await StartApplicationAsync(sessionOwnerId: "different-owner");
        using var otherOwnerClient = otherOwnerApp.GetTestClient();
        using var denied = await otherOwnerClient.GetAsync("/api/v1/ellai/sessions/session-1/voice/state");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, denied.StatusCode);
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
        var responses = app.Services.GetRequiredService<EllaiSessionResponseStore>();
        responses.Publish("session-1", "utterance-1", "Will be cleared on cancel.");

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
        Assert.Empty(responses.ReadSince("session-1", 0).Responses);
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
        string sessionOwnerId = "test-owner", string sessionStatus = "Active",
        bool hasCurrentScope = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, EllaiTestAuthenticationHandler>(
                IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, EllaiTestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { })
            .AddCookie(EllaiGuestEndpoints.AuthenticationScheme, options =>
            {
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
                options.Cookie.SameSite = SameSiteMode.Strict;
            });
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddRateLimiter(options =>
        {
            foreach (var policyName in new[] { "ellai-guest-write", "ellai-guest-read" })
            {
                options.AddPolicy(policyName, _ => RateLimitPartition.GetFixedWindowLimiter(
                    "integration-test", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 1000,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            }
        });
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
        builder.Services.AddSingleton<IEllaiUtteranceOutboxStore>(sessionStore);
        builder.Services.AddSingleton(sessionStore);
        builder.Services.AddSingleton<IScopeService>(new TestScopeService(hasCurrentScope));
        builder.Services.AddSingleton<EllaiSessionScopeResolver>();
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
        builder.Services.AddSingleton<EllaiSessionResponseStore>();
        builder.Services.AddSingleton<EllaiResponseSpeechCoordinator>();
        builder.Services.AddSingleton<EllaiVoiceTurnCoordinator>();
        builder.Services.AddHttpClient<EllaiSpeechSynthesisClient>();
        builder.Services.AddTransient<IEllaiSpeechSynthesisClient>(services =>
            services.GetRequiredService<EllaiSpeechSynthesisClient>());
        builder.Services.AddSingleton<IEllaiApprovalStore, EllaiReceiptStore>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseAntiforgery();
        app.MapEllaiEndpoints();
        app.MapEllaiGuestEndpoints();
        await app.StartAsync();
        return app;
    }

    private static ClaimsPrincipal TestPrincipal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "test-owner"),
            new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));

    private sealed class TestScopeService(bool hasCurrentScope) : IScopeService
    {
        public ValueTask<SwiftAid.Contracts.Sync.ScopeManifest> GetAsync(
            ActorContext actor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasCurrentScope)
            {
                throw new ScopeUnavailableException(ScopeFailureCode.GrantMissing);
            }

            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new SwiftAid.Contracts.Sync.ScopeManifest(
                [new SwiftAid.Contracts.Sync.ScopeGrantManifest(
                    "scope-group-1", now.AddMinutes(-1), now.AddHours(1),
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<Guid>(),
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<string>(), Array.Empty<string>(), false)],
                actor.AccessEpoch,
                now.AddHours(1)));
        }
    }
}

public sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(principal));
}

public sealed class CountingEllaiSessionStore : IEllaiSessionStore, IEllaiUtteranceOutboxStore
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
        EllaiSessionRecord nextRecord, long expectedVersion, string clientUtteranceId,
        CancellationToken ct, string? outboxPayloadJson = null) =>
        _inner.TryAcceptUtteranceAsync(nextRecord, expectedVersion, clientUtteranceId, ct, outboxPayloadJson);

    public Task SaveCheckpointAsync(EllaiCheckpoint checkpoint, CancellationToken ct) =>
        _inner.SaveCheckpointAsync(checkpoint, ct);

    public Task<EllaiUtteranceOutboxItem?> ClaimNextAsync(
        DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct) =>
        _inner.ClaimNextAsync(nowUtc, leaseExpiresAtUtc, ct);

    public Task CompleteAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset completedAtUtc, CancellationToken ct) =>
        _inner.CompleteAsync(sessionId, clientUtteranceId, attemptCount, completedAtUtc, ct);

    public Task FailAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset retryAtUtc, bool terminal, CancellationToken ct) =>
        _inner.FailAsync(sessionId, clientUtteranceId, attemptCount, retryAtUtc, terminal, ct);
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
