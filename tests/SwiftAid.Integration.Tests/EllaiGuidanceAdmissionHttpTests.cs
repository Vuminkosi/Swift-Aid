using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Sync;
using System.Net.Http.Json;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiGuidanceAdmissionHttpTests
{
    [Fact]
    public async Task HttpAdmissionKeepsUnknownProposalsAndForeignSessionsNonExecutable()
    {
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(new("owned", EllaiMode.GuideMe, "Active", 1, 1, 1, 1, 0, 12,
            "owner", "scope", null, null, DateTimeOffset.UtcNow), default);
        await store.CreateAsync(new("foreign", EllaiMode.GuideMe, "Active", 1, 1, 1, 1, 0, 12,
            "someone-else", "scope", null, null, DateTimeOffset.UtcNow), default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IEllaiSessionStore>(store);
        builder.Services.AddSingleton<IEllaiUtteranceOutboxStore>(store);
        builder.Services.AddSingleton(new EllaiUiRegistry());
        builder.Services.AddSingleton<IEllaiUiObservationStore, InMemoryEllaiUiObservationStore>();
        builder.Services.AddScoped<EllaiGuidanceProposalAdmission>();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "owner"),
                new Claim(SwiftAid.Application.Authorization.ActorContext.AccessEpochClaimType, "1")], "fixture"));
            return next(context);
        });
        Delegate handler = (Func<HttpContext, string, string, IEllaiSessionStore, EllaiGuidanceProposalAdmission, CancellationToken, Task<IResult>>)EllaiGuidanceProposalEndpoint.ReadAsync;
        app.MapGet("/sessions/{sessionId}/guidance/admission", handler);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/sessions/foreign/guidance/admission?proposalId=x")).StatusCode);
            using var own = await client.GetAsync("/sessions/owned/guidance/admission?proposalId=x");
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Contains("Stale", await own.Content.ReadAsStringAsync());
            Assert.True(own.Headers.CacheControl?.NoStore);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/sessions/owned/guidance/admission?proposalId=")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }
    [Theory]
    [InlineData("report", "report.entry", false)]
    [InlineData("navigation", "navigation.dashboard", false)]
    [InlineData("navigation", "navigation.dashboard", true)]
    public async Task PlanningPostGeneratesOnceAndReturnsDurableOwnedPlan(string feature, string control, bool navigate)
    {
        var store = new InMemoryEllaiSessionStore();
        var session = new EllaiSessionRecord("owned", EllaiMode.GuideMe, "Active", 1, 1, 1, 1, 0, 12,
            "owner", "scope", null, null, DateTimeOffset.UtcNow);
        await store.CreateAsync(session, default);
        await store.TryAcceptUtteranceAsync(session with { SessionVersion = 2 }, 1, "turn", default, "{}");
        var claim = (await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default))!;
        await store.CompleteAsync("owned", "turn", claim.AttemptCount, DateTimeOffset.UtcNow, default,
            EllaiUtteranceResolution.Completed("I can guide you.", 1) with
            {
                UiProposalsJson = EllaiUiProposalPayload.Serialize([new(UiIntentKind.StartGuidance, "ellai.guidance", null, null,
                    new Dictionary<string, string> { ["feature"] = feature, ["objective"] = "Create a report" })]),
            });
        var proposal = EllaiConversationProjection.ToClient(Assert.Single(await store.ReadConversationAsync("owned", "owner", default))).UiProposals.Single();
        var registry = new EllaiUiRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        foreach (var descriptor in EllaiNavigationManifest.Controls) registry.Register(descriptor);
        var observations = new InMemoryEllaiUiObservationStore();
        await observations.RecordAsync(new("owned", "view", "context", "web", [control], DateTimeOffset.UtcNow,
            [new(control, UiObservationState.Ready)]), default);
        var completion = new SyntheticPlan(feature, control, navigate);
        var scopes = new CurrentScopes();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IEllaiSessionStore>(store);
        builder.Services.AddSingleton<IEllaiUtteranceOutboxStore>(store);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IScopeService>(scopes);
        builder.Services.AddScoped<EllaiUiAuthorityResolver>();
        builder.Services.AddSingleton<IEllaiUiObservationStore>(observations);
        builder.Services.AddSingleton<IEllaiTurnCompletionClient>(completion);
        builder.Services.AddScoped<EllaiModelGuidancePlanner>();
        builder.Services.AddScoped<EllaiGuidanceProposalAdmission>();
        builder.Services.AddScoped<EllaiGuidancePlanValidator>();
        builder.Services.AddScoped<IEllaiUiDiscoveryService, EllaiUiDiscoveryService>();
        builder.Services.AddSingleton<EllaiUsageLedger>();
        builder.Services.AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "owner"),
                new Claim(SwiftAid.Application.Authorization.ActorContext.AccessEpochClaimType, "1")], "fixture"));
            return next(context);
        });
        Delegate handler = (Func<HttpContext, string, EllaiGuidancePlanRequest, IServiceProvider, CancellationToken, Task<IResult>>)EllaiGuidanceProposalEndpoint.PrepareAsync;
        app.MapPost("/sessions/{sessionId}/guidance/plan", handler);
        Delegate progressHandler = (Func<HttpContext, string, EllaiGuidanceStepRequest, IServiceProvider, CancellationToken, Task<IResult>>)EllaiGuidanceProposalEndpoint.CompleteStepAsync;
        app.MapPost("/sessions/{sessionId}/guidance/steps/complete", progressHandler);
        Delegate controlHandler = (Func<HttpContext, string, EllaiGuidanceControlRequest, IServiceProvider, CancellationToken, Task<IResult>>)EllaiGuidanceProposalEndpoint.ControlAsync;
        app.MapPost("/sessions/{sessionId}/guidance/control", controlHandler);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            for (var index = 0; index < 2; index++)
            {
                using var response = await client.PostAsJsonAsync("/sessions/owned/guidance/plan", new EllaiGuidancePlanRequest(proposal.ProposalId));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var plan = await response.Content.ReadFromJsonAsync<EllaiGuidancePlanDto>();
                Assert.Equal("Ready", plan!.Status);
                Assert.Equal(control, Assert.Single(plan.Plan!.Steps).ControlId);
                Assert.Equal("context", plan.ContextStamp);
                Assert.Equal(feature == "navigation" ? "nav-dashboard" : "nav-report", plan.NavigationId);
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
            using var foreign = await client.PostAsJsonAsync("/sessions/foreign/guidance/plan", new EllaiGuidancePlanRequest(proposal.ProposalId));
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.Equal(1, completion.Calls);
            var checkpoint = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("owned", default))!.GuidanceJson)!;
            await observations.RecordAsync(new("owned", "refreshed-view", "refreshed-context", "web", [control], DateTimeOffset.UtcNow,
                [new(control, UiObservationState.Ready)]), default);
            using var restoredResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/plan", new EllaiGuidancePlanRequest(proposal.ProposalId));
            var restored = (await restoredResponse.Content.ReadFromJsonAsync<EllaiGuidancePlanDto>())!;
            Assert.Equal(checkpoint.Plan!.PlanId, restored.Plan!.PlanId);
            Assert.Equal("entry", restored.CurrentStepId);
            Assert.Equal(checkpoint.Version + 1, restored.ProgressVersion);
            Assert.Equal(1, completion.Calls);
            if (feature == "navigation")
            {
                scopes.Revoked = true;
                using var revoked = await client.PostAsJsonAsync("/sessions/owned/guidance/plan", new EllaiGuidancePlanRequest(proposal.ProposalId));
                Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
                Assert.Equal(1, completion.Calls);
                scopes.Revoked = false;
            }
            var controlRequest = new EllaiGuidanceControlRequest(proposal.ProposalId, restored.Plan.PlanId, restored.ProgressVersion, "pause");
            using var pausedResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/control", controlRequest);
            var paused = (await pausedResponse.Content.ReadFromJsonAsync<EllaiGuidanceProgressDto>())!;
            Assert.Equal("Paused", paused.Status);
            using var resumedResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/control", controlRequest with { ExpectedVersion = paused.Version, Command = "resume" });
            var resumed = (await resumedResponse.Content.ReadFromJsonAsync<EllaiGuidanceProgressDto>())!;
            Assert.Equal("Ready", resumed.Status);
            restored = restored with { ProgressVersion = resumed.Version };
            using var deniedControl = await client.PostAsJsonAsync("/sessions/foreign/guidance/control", controlRequest);
            Assert.Equal(HttpStatusCode.NotFound, deniedControl.StatusCode);
            var stepRequest = new EllaiGuidanceStepRequest(proposal.ProposalId, restored.Plan.PlanId,
                restored.ProgressVersion, "entry", "refreshed-context");
            if (navigate)
            {
                // A mounted menu link alone must not satisfy navigation completion.
                foreach (var destination in new string?[] { null, "nav-account" })
                {
                    await observations.RecordAsync(new("owned", "destination-view", "destination-context", "web", [control],
                        DateTimeOffset.UtcNow, [new(control, UiObservationState.Ready)], destination), default);
                    using var rejected = await client.PostAsJsonAsync("/sessions/owned/guidance/steps/complete",
                        stepRequest with { ContextStamp = "destination-context" });
                    Assert.Equal("EvidenceRequired", (await rejected.Content.ReadFromJsonAsync<EllaiGuidanceProgressDto>())!.Status);
                }
                await observations.RecordAsync(new("owned", "destination-view", "destination-context", "web", [control],
                    DateTimeOffset.UtcNow, [new(control, UiObservationState.Ready)], "nav-dashboard"), default);
                stepRequest = stepRequest with { ContextStamp = "destination-context" };
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var progressResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/steps/complete", stepRequest);
                Assert.Equal(HttpStatusCode.OK, progressResponse.StatusCode);
                var progress = (await progressResponse.Content.ReadFromJsonAsync<EllaiGuidanceProgressDto>())!;
                Assert.Equal("Completed", progress.Status);
                Assert.Equal(restored.ProgressVersion + 1, progress.Version);
                Assert.Equal("entry", Assert.Single(progress.CompletedStepIds!));
                Assert.True(progressResponse.Headers.CacheControl?.NoStore);
            }
            using var deniedProgress = await client.PostAsJsonAsync("/sessions/foreign/guidance/steps/complete", stepRequest);
            Assert.Equal(HttpStatusCode.NotFound, deniedProgress.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class CurrentScopes : IScopeService
    {
        public bool Revoked { get; set; }
        public ValueTask<ScopeManifest> GetAsync(ActorContext actor, CancellationToken cancellationToken)
        {
            if (Revoked) throw new ScopeUnavailableException(ScopeFailureCode.GrantMissing);
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new ScopeManifest([new("grant", now.AddMinutes(-1), now.AddMinutes(5),
                ["organization"], ["workspace"], [], ["SZ"], [], [], [], ["ReadCase"], false)], actor.AccessEpoch, now.AddMinutes(5)));
        }
    }

    private sealed class SyntheticPlan(string feature, string control, bool navigate) : IEllaiTurnCompletionClient
    {
        public int Calls { get; private set; }
        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new EllaiTurnCompletion("""
                {"schemaVersion":1,"status":"Ready","objective":"Create a report","mode":"Navigation",
                 "steps":[{"stepKey":"entry","controlId":"report.entry","objectiveId":"open-report-form","behavior":"Explain",
                 "uiIntentBindingId":null,"resourceBindingKey":null,"factIds":["public-intake-no-account"],"evidenceIds":[],
                 "explanation":"This is the report form.","nextStepKey":null}],"questions":[],"stopReason":null}
                """.Replace("report.entry", control, StringComparison.Ordinal)
                    .Replace("\"behavior\":\"Explain\"", navigate ? "\"behavior\":\"RequestUiIntent\"" : "\"behavior\":\"Explain\"", StringComparison.Ordinal)
                    .Replace("\"uiIntentBindingId\":null", navigate ? "\"uiIntentBindingId\":\"NavigateFeature\"" : "\"uiIntentBindingId\":null", StringComparison.Ordinal)
                    .Replace("open-report-form", feature == "navigation" ? "open-dashboard" : "open-report-form", StringComparison.Ordinal)
                    .Replace("[\"public-intake-no-account\"]", "[]", StringComparison.Ordinal), 10, 5));
        }
    }
}
