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
using SwiftAid.Gateway.Endpoints;
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
        Delegate receiptHandler = (Func<HttpContext, string, EllaiGuidanceReceiptRequest, IServiceProvider, CancellationToken, Task<IResult>>)EllaiGuidanceProposalEndpoint.CompleteReceiptStepAsync;
        app.MapPost("/sessions/{sessionId}/guidance/receipt/complete", receiptHandler);

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
            var receiptRequest = new EllaiGuidanceReceiptRequest("proposal", "plan", 1, "step", Guid.NewGuid(), Guid.NewGuid());
            using var foreignReceipt = await client.PostAsJsonAsync("/sessions/foreign/guidance/receipt/complete", receiptRequest);
            Assert.Equal(HttpStatusCode.NotFound, foreignReceipt.StatusCode);
            using var noProof = await client.PostAsJsonAsync("/sessions/owned/guidance/receipt/complete", receiptRequest);
            Assert.Equal(HttpStatusCode.BadRequest, noProof.StatusCode);
            Assert.True(noProof.Headers.CacheControl?.NoStore);
            client.DefaultRequestHeaders.Add("X-SwiftAid-Receipt-Proof", "invalid-proof");
            using var invalidProof = await client.PostAsJsonAsync("/sessions/owned/guidance/receipt/complete", receiptRequest);
            Assert.Equal(HttpStatusCode.BadRequest, invalidProof.StatusCode);

        }
        finally { await app.StopAsync(); }
    }
    [Theory]
    [InlineData("report", "report.entry", false, false)]
    [InlineData("report", "report.entry", false, true)]
    [InlineData("navigation", "navigation.dashboard", false, false)]
    [InlineData("navigation", "navigation.dashboard", true, false)]
    [InlineData("navigation", "navigation.dashboard", true, true)]
    public async Task PlanningPostGeneratesOnceAndReturnsDurableOwnedPlan(string feature, string control, bool navigate, bool skip)
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
        var speech = new SyntheticSpeech();
        builder.Services.AddSingleton<IEllaiSpeechSynthesisClient>(speech);
        builder.Services.AddSingleton<EllaiPreparedResponseAudio>();
        builder.Services.AddSingleton<IEllaiResponseAudioPreparation>(services => services.GetRequiredService<EllaiPreparedResponseAudio>());
        builder.Services.AddHostedService(services => services.GetRequiredService<EllaiPreparedResponseAudio>());
        builder.Services.AddSingleton<EllaiSessionResponseStore>();
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
        Delegate narrationHandler = (Func<HttpContext, string, EllaiGuidanceNarrationRequest, IServiceProvider, CancellationToken, Task<IResult>>)EllaiGuidanceProposalEndpoint.NarrateAsync;
        app.MapPost("/sessions/{sessionId}/guidance/narration", narrationHandler);
        Delegate observationHandler = (Func<string, ReportUiObservationApiRequest?, IEllaiUiObservationStore, CancellationToken, Task<IResult>>)EllaiEndpoints.ReportUiObservationAsync;
        app.MapPost("/sessions/{sessionId}/ui-observations", observationHandler);
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
            if (feature == "report")
            {
                var observed = new ReportUiObservationApiRequest("owned", "view", "validation-context", "web",
                    [control], [new(control, UiObservationState.Ready)], ValidationInputIds: ["report.original-statement", "report.occurrence-date"]);
                using var observationResponse = await client.PostAsJsonAsync("/sessions/owned/ui-observations", observed);
                Assert.Equal(HttpStatusCode.OK, observationResponse.StatusCode);
                var stored = await observations.LatestAsync("owned", default);
                Assert.Equal(observed.ValidationInputIds, stored!.ValidationInputIds);
                var validationRequest = new EllaiGuidanceNarrationRequest(proposal.ProposalId, checkpoint.Plan!.PlanId, "entry", checkpoint.Version)
                    { ValidationInputId = "report.original-statement", ObservationContextStamp = "validation-context" };
                using var validationResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/narration", validationRequest);
                Assert.Equal(HttpStatusCode.OK, validationResponse.StatusCode);
                var validation = (await validationResponse.Content.ReadFromJsonAsync<EllaiSessionResponseDto>())!;
                Assert.Equal(EllaiReportValidationGuidance.Find("report.original-statement")!.Message, validation.Text);
                var prepared = app.Services.GetRequiredService<EllaiPreparedResponseAudio>();
                using (var firstAudio = await prepared.OpenAsync("owned", validation.Sequence, default).WaitAsync(TimeSpan.FromSeconds(5)))
                    Assert.NotEmpty(await firstAudio.Content.ReadAsByteArrayAsync());
                Assert.Contains(speech.Requests, item => item.RequestId.StartsWith($"owned-{validation.Sequence}-", StringComparison.Ordinal) &&
                    item.Text.Contains("Describe what happened", StringComparison.Ordinal) && item.VoiceId == "af_heart");
                using var wrongIssue = await client.PostAsJsonAsync("/sessions/owned/guidance/narration",
                    validationRequest with { ValidationInputId = "report.occurrence-date" });
                Assert.Equal(HttpStatusCode.NotFound, wrongIssue.StatusCode);
                var nextObservation = observed with { ContextStamp = "validation-next", ValidationInputIds = ["report.occurrence-date"] };
                using var nextObservationResponse = await client.PostAsJsonAsync("/sessions/owned/ui-observations", nextObservation);
                Assert.Equal(HttpStatusCode.OK, nextObservationResponse.StatusCode);
                using var staleIssue = await client.PostAsJsonAsync("/sessions/owned/guidance/narration", validationRequest);
                Assert.Equal(HttpStatusCode.NotFound, staleIssue.StatusCode);
                using var nextIssue = await client.PostAsJsonAsync("/sessions/owned/guidance/narration", validationRequest with
                    { ValidationInputId = "report.occurrence-date", ObservationContextStamp = "validation-next", EnglishVoiceId = "am_michael" });
                Assert.Equal(HttpStatusCode.OK, nextIssue.StatusCode);
                var nextValidation = (await nextIssue.Content.ReadFromJsonAsync<EllaiSessionResponseDto>())!;
                Assert.Equal(EllaiReportValidationGuidance.Find("report.occurrence-date")!.Message, nextValidation.Text);
                using (var nextAudio = await prepared.OpenAsync("owned", nextValidation.Sequence, default).WaitAsync(TimeSpan.FromSeconds(5)))
                    Assert.NotEmpty(await nextAudio.Content.ReadAsByteArrayAsync());
                Assert.Contains(speech.Requests, item => item.RequestId.StartsWith($"owned-{nextValidation.Sequence}-", StringComparison.Ordinal) &&
                    item.Text.Contains("reported date", StringComparison.Ordinal) && item.VoiceId == "am_michael");
                foreach (var inputId in new[] { "report.original-statement-too-long", "report.too-many-observations",
                             "report.observation-too-long", "report.observations-total-too-long",
                             "report.country-catalogue-unavailable", "report.privacy-notice-unavailable", "report.processing-policy-unavailable",
                             "report.invalid-draft-country", "report.invalid-exact-occurrence" })
                {
                    var contextStamp = $"context-{inputId}";
                    using var issueObservation = await client.PostAsJsonAsync("/sessions/owned/ui-observations",
                        observed with { ContextStamp = contextStamp, ValidationInputIds = [inputId, "report.occurrence-date"] });
                    Assert.Equal(HttpStatusCode.OK, issueObservation.StatusCode);
                    using var issueResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/narration", validationRequest with
                        { ValidationInputId = inputId, ObservationContextStamp = contextStamp });
                    Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
                    var issueNarration = (await issueResponse.Content.ReadFromJsonAsync<EllaiSessionResponseDto>())!;
                    Assert.Equal(EllaiReportValidationGuidance.Find(inputId)!.Message, issueNarration.Text);
                    using var laterIssue = await client.PostAsJsonAsync("/sessions/owned/guidance/narration", validationRequest with
                        { ValidationInputId = "report.occurrence-date", ObservationContextStamp = contextStamp });
                    Assert.Equal(HttpStatusCode.NotFound, laterIssue.StatusCode);
                }
            }
            await observations.RecordAsync(new("owned", "refreshed-view", "refreshed-context", "web", [control], DateTimeOffset.UtcNow,
                [new(control, UiObservationState.Ready)]), default);
            using var wrongPlanResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/plan", new EllaiGuidancePlanRequest(proposal.ProposalId)
                { ExistingPlanId = "other-plan", CurrentStepId = "entry" });
            Assert.Equal("Stale", (await wrongPlanResponse.Content.ReadFromJsonAsync<EllaiGuidancePlanDto>())!.Status);
            Assert.Equal(checkpoint.Version, EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("owned", default))!.GuidanceJson)!.Version);
            using var restoredResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/plan", new EllaiGuidancePlanRequest(proposal.ProposalId)
                { ExistingPlanId = checkpoint.Plan!.PlanId, CurrentStepId = "entry" });
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
            var narrationRequest = new EllaiGuidanceNarrationRequest(proposal.ProposalId, restored.Plan.PlanId,
                restored.CurrentStepId!, restored.ProgressVersion);
            using var narrationResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/narration", narrationRequest);
            Assert.Equal(HttpStatusCode.OK, narrationResponse.StatusCode);
            var narration = (await narrationResponse.Content.ReadFromJsonAsync<EllaiSessionResponseDto>())!;
            Assert.Equal(restored.Plan.Steps[0].Explanation, narration.Text);
            Assert.Equal("Guidance", narration.Progress?.State);
            Assert.True(narrationResponse.Headers.CacheControl?.NoStore);
            using var duplicateNarration = await client.PostAsJsonAsync("/sessions/owned/guidance/narration", narrationRequest);
            Assert.Equal(narration.Sequence, (await duplicateNarration.Content.ReadFromJsonAsync<EllaiSessionResponseDto>())!.Sequence);
            using var foreignNarration = await client.PostAsJsonAsync("/sessions/foreign/guidance/narration", narrationRequest);
            Assert.Equal(HttpStatusCode.NotFound, foreignNarration.StatusCode);
            using var pausedResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/control", controlRequest);
            var paused = (await pausedResponse.Content.ReadFromJsonAsync<EllaiGuidanceProgressDto>())!;
            Assert.Equal("Paused", paused.Status);
            using var pausedNarration = await client.PostAsJsonAsync("/sessions/owned/guidance/narration", narrationRequest);
            Assert.Equal(HttpStatusCode.NotFound, pausedNarration.StatusCode);
            using var resumedResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/control", controlRequest with { ExpectedVersion = paused.Version, Command = "resume" });
            var resumed = (await resumedResponse.Content.ReadFromJsonAsync<EllaiGuidanceProgressDto>())!;
            Assert.Equal("Ready", resumed.Status);
            restored = restored with { ProgressVersion = resumed.Version };
            using var backResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/control",
                controlRequest with { ExpectedVersion = resumed.Version, Command = "back" });
            var back = (await backResponse.Content.ReadFromJsonAsync<EllaiGuidanceProgressDto>())!;
            Assert.Equal("AtStart", back.Status);
            Assert.Equal(resumed.Version, back.Version);
            Assert.True(backResponse.Headers.CacheControl?.NoStore);

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
            if (skip)
            {
                using var skippedResponse = await client.PostAsJsonAsync("/sessions/owned/guidance/control",
                    controlRequest with { ExpectedVersion = restored.ProgressVersion, Command = "skip" });
                var skipped = (await skippedResponse.Content.ReadFromJsonAsync<EllaiGuidanceProgressDto>())!;
                Assert.Equal(navigate ? "SkipNotAllowed" : "Completed", skipped.Status);
                Assert.Empty(skipped.CompletedStepIds!);
                if (!navigate)
                {
                    Assert.Equal("entry", Assert.Single(skipped.SkippedStepIds!));
                    var persisted = EllaiGuidanceCheckpoint.Deserialize((await store.GetAsync("owned", default))!.GuidanceJson)!;
                    Assert.Empty(persisted.CompletedStepIds);
                    Assert.Equal("entry", Assert.Single(persisted.SkippedStepIds));
                    return;
                }
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

    private sealed class SyntheticSpeech : IEllaiSpeechSynthesisClient
    {
        public bool IsConfigured => true;
        public System.Collections.Concurrent.ConcurrentQueue<(string RequestId, string Text, string? VoiceId)> Requests { get; } = new();
        public Task<HttpResponseMessage> SynthesizeAsync(string requestId, string text, CancellationToken ct) =>
            SynthesizeAsync(requestId, text, "eng", "af_heart", ct);
        public Task<HttpResponseMessage> SynthesizeAsync(string requestId, string text, string language, string? voiceId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Enqueue((requestId, text, voiceId));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Voice-Id", voiceId ?? "test");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            return Task.FromResult(response);
        }
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
