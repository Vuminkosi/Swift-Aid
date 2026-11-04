using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Intelligence.Meta;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class ReportVoiceEndpointTests
{
    [Theory]
    [InlineData(true, "/api/v1/ellai/guest/sessions/session/report-voice/extract")]
    [InlineData(false, "/api/v1/ellai/sessions/session/report-voice/extract")]
    public async Task ExistingHttpVoiceClientUsesMatchingSessionRouteAndAntiforgery(bool guest, string expectedPath)
    {
        var handler = new FixtureHttpHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, guest);

        Assert.True(await client.GrantReportVoiceConsentAsync("session", default));
        var result = await client.ExtractReportVoiceAsync("session", "A fever", default);

        Assert.Equal("Ready", result!.Status);
        Assert.Equal(expectedPath, handler.LastPath);
        Assert.Equal("fixture-token", handler.LastAntiforgery);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportRecordingUsesExistingTranscriptionRouteWithoutSubmittingConversation(bool guest)
    {
        var handler = new FixtureHttpHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, guest);

        await client.TranscribeReportVoiceAsync("session", [1, 2], EllaiVoiceCaptureMode.Recorded, default, "capture");
        Assert.Contains("serverSubmission=false", handler.LastQuery);
        await client.TranscribeVoiceAsync("session", [1, 2], EllaiVoiceCaptureMode.Recorded, default, "capture");
        Assert.Contains("serverSubmission=true", handler.LastQuery);
        var socket = ((IEllaiReviewClient)client).CreateReportVoiceWebSocketUri(new Uri("https://localhost/"), "session", "capture");
        Assert.Contains("serverSubmission=false", socket.Query);
        Assert.Contains(guest ? "/guest/" : "/ellai/sessions/", socket.AbsolutePath);
    }

    [Theory]
    [InlineData(true, "/api/v1/ellai/guest/sessions/session/report-voice/saved-recording")]
    [InlineData(false, "/api/v1/ellai/sessions/session/report-voice/saved-recording")]
    public async Task SavedRecordingUsesReportRouteAndAntiforgery(bool guest, string expectedPath)
    {
        var handler = new FixtureHttpHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, guest);
        await client.TranscribeSavedReportVoiceAsync("session", [1, 2], default);
        Assert.Equal(expectedPath, handler.LastPath);
        Assert.Equal("fixture-token", handler.LastAntiforgery);
        Assert.Equal("audio/wav", handler.LastContentType);
    }

    [Fact]
    public async Task HttpRequiresConsentAndRejectsOtherOwnersAndCookieWithoutAntiforgery()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        var sessions = new InMemoryEllaiSessionStore();
        await sessions.CreateAsync(new EllaiSessionRecord("session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 10, "owner", "scope", null, null, DateTimeOffset.UtcNow), default);
        await sessions.CreateAsync(new EllaiSessionRecord("other", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 10, "other-owner", "scope", null, null, DateTimeOffset.UtcNow), default);
        builder.Services.AddSingleton<IEllaiSessionStore>(sessions);
        builder.Services.AddSingleton<EllaiConsentService>();
        builder.Services.AddSingleton<IEllaiConsentStore, InMemoryEllaiConsentStore>();
        builder.Services.AddSingleton<EllaiUsageLedger>();
        builder.Services.AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        builder.Services.AddSingleton<IEllaiTurnCompletionClient, FixtureCompletion>();
        var options = new EllaiOptions { Enabled = true, ApiKey = "synthetic", AsrModel = "fixture", AsrTranscriptionUrl = "https://asr.invalid/transcribe" };
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new MetaAsrUploadAdapter(new HttpClient(new FixtureAsrHandler()), options));
        builder.Services.AddScoped<ReportVoiceExtraction>();
        builder.Services.AddScoped<ReportVoiceExtractionAdmission>();
        builder.Services.AddScoped<ReportVoiceGatewayService>();
        builder.Services.AddScoped<EllaiVoiceTranscriptionService>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var group = app.MapGroup("/sessions/{sessionId}").RequireAuthorization();
        group.MapPost("/consent", ReportVoiceEndpoints.ConsentAsync);
        group.MapPost("/extract", ReportVoiceEndpoints.ExtractAsync);
        group.MapPost("/saved", ReportVoiceEndpoints.TranscribeSavedAsync);
        await app.StartAsync();
        using var client = app.GetTestClient();

        static ByteArrayContent Wave()
        {
            var bytes = new byte[44];
            "RIFF"u8.CopyTo(bytes);
            "WAVE"u8.CopyTo(bytes.AsSpan(8));
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return content;
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/sessions/other/saved", Wave())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/sessions/session/saved", new ByteArrayContent([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/sessions/session/saved", Wave())).StatusCode);
        var beforeConsent = await client.PostAsJsonAsync("/sessions/session/extract", new ReportVoiceExtractionRequest("A fever"));
        Assert.Equal("ConsentRequired", (await beforeConsent.Content.ReadFromJsonAsync<ReportVoiceExtractionResponse>())!.Status);
        var foreign = await client.PostAsJsonAsync("/sessions/other/consent", new ReportVoiceConsentRequest(true));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Post, "/sessions/session/consent")
        {
            Content = JsonContent.Create(new ReportVoiceConsentRequest(true))
        };
        csrfRequest.Headers.Add("Cookie", "synthetic=1");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(csrfRequest)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/sessions/session/consent", new ReportVoiceConsentRequest(true))).StatusCode);
        var voice = new EllaiVoiceTranscriptionService(app.Services.GetRequiredService<EllaiConsentService>(),
            app.Services.GetRequiredService<IEllaiConsentStore>(), app.Services.GetRequiredService<EllaiOptions>(), app.Services);
        Assert.True(await voice.HasSavedReportConsentAsync("session", default));
        Assert.False(await voice.HasConsentAsync("session", default));
        var transcription = await client.PostAsync("/sessions/session/saved", Wave());
        Assert.Equal(HttpStatusCode.OK, transcription.StatusCode);
        var transcript = await transcription.Content.ReadFromJsonAsync<EllaiVoiceTranscriptionResult>();
        Assert.Equal("A fever", transcript!.Transcript);
        Assert.False(transcript.ServerSubmission);
        var response = await client.PostAsJsonAsync("/sessions/session/extract", new ReportVoiceExtractionRequest("A fever"));
        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());
        var result = await response.Content.ReadFromJsonAsync<ReportVoiceExtractionResponse>();
        Assert.Equal("Ready", result!.Status);
        Assert.Equal("fever", Assert.Single(result.Suggestions).Value);
    }

    private sealed class FixtureAsrHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { transcript = "A fever" })
            });
    }

    private sealed class FixtureCompletion : IEllaiTurnCompletionClient
    {
        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
            Task.FromResult(new EllaiTurnCompletion("""{"fields":[{"field":"observation","value":"fever","evidence":"A fever"}]}""", 10, 5));
    }

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastAntiforgery { get; private set; }
        public string? LastQuery { get; private set; }
        public string? LastContentType { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { requestToken = "fixture-token" })
                });
            LastPath = request.RequestUri!.AbsolutePath;
            LastQuery = request.RequestUri.Query;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            LastAntiforgery = request.Headers.TryGetValues("X-CSRF-TOKEN", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ReportVoiceExtractionResponse("Ready", []))
            });
        }
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "owner"),
                new Claim(ActorContext.AccessEpochClaimType, "1")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
