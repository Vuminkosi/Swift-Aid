using System.Security.Claims;
using System.Text;
using System.Net.Http.Json;
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
using Microsoft.Extensions.AI;
using SwiftAid.Intelligence.Meta;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiPrivilegedRouteAuthorizationTests
{
    private sealed class LiveFrameworkHttpFactAttribute : FactAttribute
    {
        public LiveFrameworkHttpFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
                Environment.GetEnvironmentVariable("SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES") != "1")
                Skip = "Requires the configured live Muse route and dedicated disposable PostgreSQL fixture.";
        }
    }

    [LiveFrameworkHttpFact]
    public async Task GuestHttpFrameworkUsesLiveMuseAndDurableMultiTurnHistory()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        string? sessionId = null;
        try
        {
            await using var host = await StartApplicationAsync(frameworkWorker: true, frameworkFixture: fixture, liveModel: true);
            using var http = host.GetTestClient();
            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            void Merge(HttpResponseMessage response)
            {
                if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
                foreach (var value in values)
                {
                    var pair = value.Split(';', 2)[0];
                    var index = pair.IndexOf('=');
                    if (index > 0) cookies[pair[..index]] = pair[(index + 1)..];
                }
            }
            async Task<HttpResponseMessage> PostAsync<T>(string path, T body, string token)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(item => $"{item.Key}={item.Value}")));
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
                return await http.SendAsync(request);
            }
            using var antiforgery = await http.GetAsync("/api/v1/ellai/guest/antiforgery");
            Merge(antiforgery);
            var token = (await antiforgery.Content.ReadFromJsonAsync<EllaiGuestAntiforgeryToken>())!.RequestToken;
            using var started = await PostAsync("/api/v1/ellai/guest/sessions",
                new StartSessionRequest(EllaiMode.Ask, "live-framework-http", null, null, null, null), token);
            Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);
            Merge(started);
            var session = (await started.Content.ReadFromJsonAsync<EllaiSessionView>())!;
            sessionId = session.SessionId;
            async Task<(EllaiSessionView Session, string Answer)> SendTurnAsync(string id, string text, EllaiSessionView current, int ordinal)
            {
                using var submitted = await PostAsync($"/api/v1/ellai/guest/sessions/{sessionId}/utterances",
                    new SubmitUtteranceRequest(id, sessionId, current.SessionVersion, text, null, "typed", ordinal), token);
                Assert.Equal(System.Net.HttpStatusCode.OK, submitted.StatusCode);
                var accepted = (await submitted.Content.ReadFromJsonAsync<EllaiSessionView>())!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                while (true)
                {
                    await Task.Delay(100, timeout.Token);
                    await using var db = fixture.CreateDbContext();
                    var store = new PostgresEllaiSessionStore(db);
                    var saved = (await store.GetAsync(sessionId, timeout.Token))!;
                    var turn = (await store.ReadConversationAsync(sessionId, saved.OwnerActorId!, timeout.Token))
                        .Single(item => item.ClientUtteranceId == id);
                    if (turn.CompletedAtUtc is null) continue;
                    Assert.Equal("Completed", turn.Outcome);
                    Assert.False(string.IsNullOrWhiteSpace(turn.AssistantText));
                    return (accepted, turn.AssistantText!);
                }
            }
            var first = await SendTurnAsync("live-turn-1", "Remember the word cobalt for our conversation. Reply briefly.", session, 1);
            var second = await SendTurnAsync("live-turn-2", "What word did I ask you to remember?", first.Session, 2);
            Assert.Contains("cobalt", second.Answer, StringComparison.OrdinalIgnoreCase);
            using var feedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/ellai/guest/sessions/{sessionId}/responses?cursor=0");
            feedRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(item => $"{item.Key}={item.Value}")));
            using var feed = await http.SendAsync(feedRequest);
            Assert.Equal(System.Net.HttpStatusCode.OK, feed.StatusCode);
            var page = (await feed.Content.ReadFromJsonAsync<EllaiSessionResponsePageDto>())!;
            Assert.Contains(page.Responses, item => item.ClientUtteranceId == "live-turn-2" && !item.IsFinal && !string.IsNullOrWhiteSpace(item.Text));
            await using var verify = fixture.CreateDbContext();
            var usage = await new PostgresEllaiUsageStore(verify).TotalsAsync(sessionId, default);
            Assert.True(usage.Calls >= 2);
            Assert.True(usage.TotalTokens > 0);
        }
        finally
        {
            if (sessionId is not null)
            {
                await using var db = fixture.CreateDbContext();
                await db.Set<EllaiUsageSampleEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
                await db.Set<EllaiConsentGrantEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
                await db.Set<EllaiUtteranceEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
                await db.Set<EllaiCheckpointEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
                await db.Set<EllaiSessionEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            }
        }
    }
    [FrameworkPostgresFact]
    public async Task GuestHttpConversationRecoversAfterTestHostRestartWithPostgres()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        var protection = new EphemeralDataProtectionProvider();
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sessionId = null;
        void Merge(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
            foreach (var value in values)
            {
                var pair = value.Split(';', 2)[0];
                var index = pair.IndexOf('=');
                if (index > 0) cookies[pair[..index]] = pair[(index + 1)..];
            }
        }
        string CookieHeader() => string.Join("; ", cookies.Select(item => $"{item.Key}={item.Value}"));
        async Task<HttpResponseMessage> PostAsync<T>(HttpClient http, string path, T body, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("Cookie", CookieHeader());
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
            return await http.SendAsync(request);
        }
        async Task WaitForTurnAsync(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await Task.Delay(25, timeout.Token);
                await using var db = fixture.CreateDbContext();
                var store = new PostgresEllaiSessionStore(db);
                var session = (await store.GetAsync(sessionId!, timeout.Token))!;
                var turn = (await store.ReadConversationAsync(sessionId!, session.OwnerActorId!, timeout.Token))
                    .Single(item => item.ClientUtteranceId == id);
                if (turn.CompletedAtUtc is null) continue;
                Assert.Equal("Completed", turn.Outcome);
                Assert.Equal("Synthetic HTTP answer.", turn.AssistantText);
                return;
            }
        }
        try
        {
            EllaiSessionView accepted;
            await using (var firstHost = await StartApplicationAsync(frameworkWorker: true, frameworkFixture: fixture,
                protection: protection))
            {
                using var http = firstHost.GetTestClient();
                using var antiforgery = await http.GetAsync("/api/v1/ellai/guest/antiforgery");
                Merge(antiforgery);
                var token = (await antiforgery.Content.ReadFromJsonAsync<EllaiGuestAntiforgeryToken>())!.RequestToken;
                using var started = await PostAsync(http, "/api/v1/ellai/guest/sessions",
                    new StartSessionRequest(EllaiMode.Ask, "durable-http", null, null, null, null), token);
                Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);
                Merge(started);
                var session = (await started.Content.ReadFromJsonAsync<EllaiSessionView>())!;
                sessionId = session.SessionId;
                using var submitted = await PostAsync(http, $"/api/v1/ellai/guest/sessions/{sessionId}/utterances",
                    new SubmitUtteranceRequest("durable-turn-1", sessionId, session.SessionVersion, "First durable question", null, "typed", 1), token);
                Assert.Equal(System.Net.HttpStatusCode.OK, submitted.StatusCode);
                accepted = (await submitted.Content.ReadFromJsonAsync<EllaiSessionView>())!;
                await WaitForTurnAsync("durable-turn-1");
            }
            await using (var secondHost = await StartApplicationAsync(frameworkWorker: true, frameworkFixture: fixture,
                protection: protection))
            {
                using var http = secondHost.GetTestClient();
                using var feedRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"/api/v1/ellai/guest/sessions/{sessionId}/responses?cursor=999&streamId=old-process");
                feedRequest.Headers.TryAddWithoutValidation("Cookie", CookieHeader());
                using var feed = await http.SendAsync(feedRequest);
                Assert.Equal(System.Net.HttpStatusCode.OK, feed.StatusCode);
                Assert.Contains("Synthetic HTTP answer.", await feed.Content.ReadAsStringAsync());
                using var stranger = await http.GetAsync($"/api/v1/ellai/guest/sessions/{sessionId}/responses?cursor=0");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, stranger.StatusCode);
                using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ellai/guest/antiforgery");
                tokenRequest.Headers.TryAddWithoutValidation("Cookie", CookieHeader());
                using var antiforgery = await http.SendAsync(tokenRequest);
                Merge(antiforgery);
                var token = (await antiforgery.Content.ReadFromJsonAsync<EllaiGuestAntiforgeryToken>())!.RequestToken;
                using var submitted = await PostAsync(http, $"/api/v1/ellai/guest/sessions/{sessionId}/utterances",
                    new SubmitUtteranceRequest("durable-turn-2", sessionId!, accepted.SessionVersion, "Second durable question", null, "typed", 2), token);
                Assert.Equal(System.Net.HttpStatusCode.OK, submitted.StatusCode);
                await WaitForTurnAsync("durable-turn-2");
                var model = secondHost.Services.GetRequiredService<FrameworkHttpClient>();
                Assert.Contains("First durable question", model.LastMessages);
                Assert.Contains("Synthetic HTTP answer.", model.LastMessages);
                Assert.Contains("Second durable question", model.LastMessages);
            }
        }
        finally
        {
            if (sessionId is not null)
            {
                await using var db = fixture.CreateDbContext();
                await db.Set<EllaiUsageSampleEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
                await db.Set<EllaiConsentGrantEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
                await db.Set<EllaiUtteranceEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
                await db.Set<EllaiCheckpointEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
                await db.Set<EllaiSessionEntity>().Where(item => item.SessionId == sessionId).ExecuteDeleteAsync();
            }
        }
    }
    [Fact]
    public async Task GuestHttpTurnsReachFrameworkWorkerAndRetainConversation()
    {
        await using var app = await StartApplicationAsync(frameworkWorker: true);
        using var client = app.GetTestClient();
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        void Merge(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
            foreach (var value in values)
            {
                var pair = value.Split(';', 2)[0];
                var index = pair.IndexOf('=');
                if (index > 0) cookies[pair[..index]] = pair[(index + 1)..];
            }
        }
        string CookieHeader() => string.Join("; ", cookies.Select(item => $"{item.Key}={item.Value}"));
        async Task<HttpResponseMessage> PostAsync<T>(string path, T body, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("Cookie", CookieHeader());
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
            return await client.SendAsync(request);
        }
        using var antiforgery = await client.GetAsync("/api/v1/ellai/guest/antiforgery");
        Assert.Equal(System.Net.HttpStatusCode.OK, antiforgery.StatusCode);
        Merge(antiforgery);
        var token = (await antiforgery.Content.ReadFromJsonAsync<EllaiGuestAntiforgeryToken>())!.RequestToken;
        using var started = await PostAsync("/api/v1/ellai/guest/sessions",
            new StartSessionRequest(EllaiMode.Ask, "framework-http", null, null, null, null), token);
        Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);
        Merge(started);
        var session = (await started.Content.ReadFromJsonAsync<EllaiSessionView>())!;
        var store = app.Services.GetRequiredService<CountingEllaiSessionStore>();
        async Task<EllaiSessionView> SendTurnAsync(string id, string text, EllaiSessionView current, int ordinal)
        {
            using var response = await PostAsync($"/api/v1/ellai/guest/sessions/{session.SessionId}/utterances",
                new SubmitUtteranceRequest(id, session.SessionId, current.SessionVersion, text, null, "typed", ordinal), token);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            var accepted = (await response.Content.ReadFromJsonAsync<EllaiSessionView>())!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(25, timeout.Token);
                var saved = (await store.GetAsync(session.SessionId, timeout.Token))!;
                turn = (await store.ReadConversationAsync(session.SessionId, saved.OwnerActorId!, timeout.Token))
                    .Single(item => item.ClientUtteranceId == id);
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("Completed", turn.Outcome);
            Assert.Equal("Synthetic HTTP answer.", turn.AssistantText);
            return accepted;
        }
        var first = await SendTurnAsync("http-turn-1", "First question", session, 1);
        await SendTurnAsync("http-turn-2", "Follow-up question", first, 2);
        var model = app.Services.GetRequiredService<FrameworkHttpClient>();
        Assert.Contains("First question", model.LastMessages);
        Assert.Contains("Synthetic HTTP answer.", model.LastMessages);
        Assert.Contains("Follow-up question", model.LastMessages);
        using var feedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/ellai/guest/sessions/{session.SessionId}/responses?cursor=0");
        feedRequest.Headers.TryAddWithoutValidation("Cookie", CookieHeader());
        using var feed = await client.SendAsync(feedRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, feed.StatusCode);
        Assert.Contains("Synthetic HTTP answer.", await feed.Content.ReadAsStringAsync());
        using var stranger = await client.GetAsync($"/api/v1/ellai/guest/sessions/{session.SessionId}/responses?cursor=0");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, stranger.StatusCode);
    }

    private sealed class FrameworkHttpClient : IChatClient
    {
        public IReadOnlyList<string> LastMessages { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            LastMessages = messages.Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
                .Select(message => message.Text).ToArray();
            yield return new ChatResponseUpdate(ChatRole.Assistant, """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic HTTP answer.","spokenText":"Synthetic HTTP answer.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 4 })]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    [Fact]
    public async Task SessionHistoryListsOnlyTheCurrentOwnersSessions()
    {
        await using var app = await StartApplicationAsync();
        var store = app.Services.GetRequiredService<CountingEllaiSessionStore>();
        var owned = (await store.GetAsync("session-1", CancellationToken.None))!;
        await store.CreateAsync(owned with { SessionId = "other-private-session", OwnerActorId = "someone-else" }, CancellationToken.None);
        using var response = await app.GetTestClient().GetAsync("/api/v1/ellai/sessions?offset=0");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("session-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("other-private-session", json, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task DurableFeedRecoversCommittedAnswerWhenTransientPublicationIsMissing()
    {
        await using var app = await StartApplicationAsync();
        var store = app.Services.GetRequiredService<CountingEllaiSessionStore>();
        var session = (await store.GetAsync("session-1", CancellationToken.None))!;
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
            "durable-1", CancellationToken.None, "{\"utterance\":\"Synthetic question\"}");
        var now = DateTimeOffset.UtcNow;
        var claim = (await store.ClaimNextAsync(now, now.AddMinutes(1), CancellationToken.None))!;
        await store.CompleteAsync(session.SessionId, claim.ClientUtteranceId, claim.AttemptCount, now,
            CancellationToken.None, EllaiUtteranceResolution.Completed("Synthetic response", session.InteractionEpoch));
        var response = await app.GetTestClient().GetAsync("/api/v1/ellai/sessions/session-1/responses?cursor=999&streamId=old-process");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<EllaiSessionResponsePageDto>())!;
        Assert.Empty(page.Responses);
        Assert.NotEmpty(page.StreamId!);
        var turn = Assert.Single(page.Conversation!);
        Assert.Equal("Synthetic question", turn.UserText);
        Assert.Equal("Synthetic response", turn.AssistantText);
        Assert.Equal("Completed", turn.Outcome);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var restored = await app.GetTestClient().GetFromJsonAsync<EllaiSessionView>("/api/v1/ellai/sessions/session-1");
        Assert.Equal(2, restored!.SessionVersion);
    }

    [Fact]
    public async Task ConversationAndResumeRejectAnotherOwner()
    {
        await using var app = await StartApplicationAsync(sessionOwnerId: "another-owner");
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await app.GetTestClient().GetAsync("/api/v1/ellai/sessions/session-1/responses?cursor=0")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await app.GetTestClient().GetAsync("/api/v1/ellai/sessions/session-1")).StatusCode);
    }

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

        var submitted = await client.SubmitUtteranceAsync(new SubmitUtteranceRequest(
            "server-guest-question", started.Session.SessionId, started.Session.SessionVersion,
            "What does an intake receipt mean?", null, "typed", 1), CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.Accepted, submitted.Status);
        Assert.True(await client.ReportUiObservationAsync(
            started.Session.SessionId,
            new UiObservationReport("view", "context", "web", ["public-home"]),
            CancellationToken.None));

        var outbox = await app.Services.GetRequiredService<IEllaiUtteranceOutboxStore>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.NotNull(outbox);
        Assert.Equal("What does an intake receipt mean?",
            EllaiUtteranceWorkEnvelope.Deserialize(outbox!.PayloadJson).Utterance);
        var cancelled = await client.CancelSessionAsync(started.Session.SessionId,
            submitted.Session!.SessionVersion, CancellationToken.None);
        Assert.Equal(EllaiClientOperationStatus.Cancelled, cancelled.Status);
        await client.StartSessionAsync(EllaiMode.Ask, CancellationToken.None);
        var history = await client.ListSessionHistoryAsync(0, CancellationToken.None);
        Assert.Equal(2, history!.Sessions.Count);
        Assert.NotNull(await client.PollResponsesAsync(started.Session.SessionId, 0, CancellationToken.None));
        Assert.Null(await client.PollResponsesAsync("session-1", 0, CancellationToken.None));
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
    public async Task MixedCaseUnknownUtteranceIsAcceptedForDynamicReasoning()
    {
        await using var app = await StartApplicationAsync();
        var request = new SubmitUtteranceRequest(
            "utterance-1", "session-1", 1,
            "patient case 4812 narrative", null, "typed", 1);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/API/V1/ELLAI/SESSIONS/session-1/UTTERANCES", request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var session = await app.Services.GetRequiredService<IEllaiSessionStore>()
            .GetAsync("session-1", CancellationToken.None);
        Assert.Equal(2, session!.SessionVersion);
        Assert.Equal(1, session.CyclesUsed);
        var outbox = await app.Services.GetRequiredService<IEllaiUtteranceOutboxStore>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.NotNull(outbox);
        Assert.Equal(request.FinalizedText,
            EllaiUtteranceWorkEnvelope.Deserialize(outbox!.PayloadJson).Utterance);
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
        Assert.Equal("What does an intake receipt mean?", work.Utterance);
        Assert.Equal(
            [EllaiToolNames.IngestionSchema, EllaiToolNames.Locations, EllaiToolNames.ProductFacts,
                EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve, EllaiToolNames.ValidateObservations],
            work.AllowedTools);
    }

    [Fact]
    public async Task GuestCanUsePublicHelpWithoutGettingWorkspaceTools()
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
            Assert.Equal(EllaiSessionService.GuestCyclesAllowed, session.CyclesAllowed);

            using (var secondStart = await SendGuestPostAsync(client, cookies, token,
                "/api/v1/ellai/guest/sessions",
                new StartSessionRequest(EllaiMode.Ask, "guest-start-2", null, null, null, null)))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, secondStart.StatusCode);
                MergeCookies(cookies, secondStart);
                var second = (await secondStart.Content.ReadFromJsonAsync<EllaiSessionView>())!;
                using var cancelled = await SendGuestPostAsync(client, cookies, token,
                    $"/api/v1/ellai/guest/sessions/{second.SessionId}/cancel",
                    new CancelEllaiSessionApiRequest(second.SessionVersion));
                Assert.Equal(System.Net.HttpStatusCode.OK, cancelled.StatusCode);
                MergeCookies(cookies, cancelled);
                using var historyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ellai/guest/sessions?offset=0");
                historyRequest.Headers.TryAddWithoutValidation("Cookie",
                    string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
                using var historyResponse = await client.SendAsync(historyRequest);
                Assert.Equal(System.Net.HttpStatusCode.OK, historyResponse.StatusCode);
                var history = (await historyResponse.Content.ReadFromJsonAsync<EllaiSessionHistoryPageDto>())!;
                Assert.Equal(2, history.Sessions.Count);
                Assert.Contains(history.Sessions, item => item.SessionId == session.SessionId);
                Assert.Contains(history.Sessions, item => item.SessionId == second.SessionId);
                Assert.DoesNotContain(history.Sessions, item => item.SessionId == "session-1");
            }

            using (var stateRequest = new HttpRequestMessage(HttpMethod.Get,
                       $"/api/v1/ellai/guest/sessions/{session.SessionId}/voice/state"))
            {
                stateRequest.Headers.TryAddWithoutValidation("Cookie",
                    string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
                using var stateResponse = await client.SendAsync(stateRequest);
                Assert.Equal(System.Net.HttpStatusCode.OK, stateResponse.StatusCode);
                var stateJson = await stateResponse.Content.ReadAsStringAsync();
                Assert.Contains("no-store", stateResponse.Headers.CacheControl?.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("private guest words", stateJson, StringComparison.Ordinal);
                var voiceState = await stateResponse.Content.ReadFromJsonAsync<EllaiVoiceTurnStatusDto>();
                Assert.Equal("Idle", voiceState?.State);
            }

            for (var poll = 0; poll < 125; poll++)
            {
                using var voiceStateRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"/api/v1/ellai/guest/sessions/{session.SessionId}/voice/state");
                voiceStateRequest.Headers.TryAddWithoutValidation("Cookie",
                    string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
                using var voiceStateResponse = await client.SendAsync(voiceStateRequest);
                Assert.Equal(System.Net.HttpStatusCode.OK, voiceStateResponse.StatusCode);
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
            Assert.Equal(
                [EllaiToolNames.IngestionSchema, EllaiToolNames.Locations, EllaiToolNames.ProductFacts,
                    EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve, EllaiToolNames.ValidateObservations],
                envelope.AllowedTools);
            Assert.Equal("What does an intake receipt mean?", envelope.Utterance);

            for (var turn = 2; turn <= EllaiSessionService.DefaultCyclesAllowed + 1; turn++)
            {
                using var anotherRequest = await SendGuestPostAsync(client, cookies, token,
                    $"/api/v1/ellai/guest/sessions/{session.SessionId}/utterances",
                    new SubmitUtteranceRequest($"guest-question-{turn}", session.SessionId, accepted.SessionVersion,
                        "Tell me a joke.", null, "typed", turn));
                Assert.Equal(System.Net.HttpStatusCode.OK, anotherRequest.StatusCode);
                accepted = (await anotherRequest.Content.ReadFromJsonAsync<EllaiSessionView>())!;
            }

            var afterSecondRequest = await app.Services.GetRequiredService<IEllaiSessionStore>()
                .GetAsync(session.SessionId, CancellationToken.None);
            Assert.Equal(EllaiSessionService.DefaultCyclesAllowed + 1, afterSecondRequest!.CyclesUsed);
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
    public async Task AuthenticatedVoiceTurnStatusIsSessionOwnerScopedAndNoStore()
    {
        await using var app = await StartApplicationAsync();
        var coordinator = app.Services.GetRequiredService<EllaiVoiceTurnCoordinator>();
        using var stream = coordinator.BeginStream("session-1", CancellationToken.None);
        Assert.True(stream.ObservePartialTranscript("turn-1", "private transcript"));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/v1/ellai/sessions/session-1/voice/state");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        var status = await response.Content.ReadFromJsonAsync<EllaiVoiceTurnStatusDto>();
        Assert.Equal("Listening", status?.State);
        Assert.Equal(stream.Epoch, status?.Epoch);
        Assert.Equal("private transcript", status?.PartialTranscript);

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
        bool hasCurrentScope = false, bool frameworkWorker = false,
        CaseAuthorityPostgresFixture? frameworkFixture = null, IDataProtectionProvider? protection = null,
        bool liveModel = false)
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
        if (protection is null) builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        else builder.Services.AddSingleton(protection);
        builder.Services.AddRateLimiter();
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
        if (frameworkFixture is null)
        {
            builder.Services.AddSingleton<IEllaiSessionStore>(sessionStore);
            builder.Services.AddSingleton<IEllaiUtteranceOutboxStore>(sessionStore);
        }
        else
        {
            builder.Services.AddScoped(_ => frameworkFixture.CreateDbContext());
            builder.Services.AddScoped<PostgresEllaiSessionStore>();
            builder.Services.AddScoped<IEllaiSessionStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
            builder.Services.AddScoped<IEllaiUtteranceOutboxStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        }
        builder.Services.AddSingleton(sessionStore);
        builder.Services.AddSingleton<IScopeService>(new TestScopeService(hasCurrentScope));
        builder.Services.AddSingleton<EllaiSessionScopeResolver>();
        builder.Services.AddSingleton<EllaiConsentService>();
        builder.Services.AddSingleton<IEllaiConsentStore, InMemoryEllaiConsentStore>();
        builder.Services.AddSingleton<EllaiUtteranceQueue>();
        if (frameworkFixture is null)
        {
            builder.Services.AddSingleton<EllaiUtteranceSubmissionService>();
            builder.Services.AddSingleton<EllaiSessionService>();
        }
        else
        {
            builder.Services.AddScoped<EllaiUtteranceSubmissionService>();
            builder.Services.AddScoped<EllaiSessionService>();
        }
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
        if (frameworkWorker)
        {
            builder.Services.AddSingleton<FrameworkHttpClient>();
            builder.Services.AddSingleton<EllaiUsageLedger>();
            if (frameworkFixture is null) builder.Services.AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
            else builder.Services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
            builder.Services.AddScoped(_ => new EllaiPromptBuilder("Synthetic test policy", "fixture"));
            builder.Services.AddScoped(sp => new EllaiToolBroker(new EllaiPolicyService(), sp.GetRequiredService<EllaiConsentService>()));
            var modelOptions = liveModel ? EllaiOptions.Bind(Environment.GetEnvironmentVariable) : new EllaiOptions
            {
                Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture"
            };
            builder.Services.AddEllaiReasoningExecutor(modelOptions, "framework");
            if (!liveModel)
                builder.Services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey,
                    (sp, _) => sp.GetRequiredService<FrameworkHttpClient>());
            builder.Services.AddHostedService<EllaiUtteranceWorker>();
        }

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
    public Task<IReadOnlyList<EllaiSessionRecord>> ListOwnedAsync(string ownerActorId, int offset, int take, CancellationToken ct) =>
        _inner.ListOwnedAsync(ownerActorId, offset, take, ct);

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

    public Task<EllaiUtteranceResolution?> CompleteAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset completedAtUtc, CancellationToken ct, EllaiUtteranceResolution? resolution = null) =>
        _inner.CompleteAsync(sessionId, clientUtteranceId, attemptCount, completedAtUtc, ct, resolution);

    public Task<EllaiConversationTurn?> ReadLatestGuidanceTurnAsync(string sessionId, string ownerActorId, CancellationToken ct) =>
        _inner.ReadLatestGuidanceTurnAsync(sessionId, ownerActorId, ct);

    public Task<IReadOnlyList<EllaiConversationTurn>> ReadConversationAsync(string sessionId, string ownerActorId, CancellationToken ct) =>
        _inner.ReadConversationAsync(sessionId, ownerActorId, ct);

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
