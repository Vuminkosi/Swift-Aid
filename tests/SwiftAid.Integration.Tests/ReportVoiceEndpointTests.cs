using System.Buffers.Binary;
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
        var consentHandler = ReportVoiceEndpoints.ConsentAsync;
        group.MapPost("/consent", consentHandler);
        group.MapPost("/checked-consent", consentHandler).AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Items[EllaiGuestEndpoints.ValidatedAntiforgeryRequest] = true;
            return await next(context);
        });
        var extractHandler = ReportVoiceEndpoints.ExtractAsync;
        group.MapPost("/extract", extractHandler);
        var savedHandler = ReportVoiceEndpoints.TranscribeSavedAsync;
        group.MapPost("/saved", savedHandler);
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

        using var checkedRequest = new HttpRequestMessage(HttpMethod.Post, "/sessions/session/checked-consent")
        { Content = JsonContent.Create(new ReportVoiceConsentRequest(true)) };
        checkedRequest.Headers.Add("Cookie", "synthetic=1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(checkedRequest)).StatusCode);
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

    [SyntheticGatewayLiveFact]
    public async Task SyntheticRecordedAndOpenLiveStreamsTraverseGatewayAndProvider()
    {
        var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
        Assert.Empty(options.Validate());
        var wav = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("SWIFTAID_TEST_SYNTHETIC_REPORT_WAV")!);
        Assert.True(wav.Length > 44 && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            wav.AsSpan(36, 4).SequenceEqual("data"u8) &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20, 2)) == 1 &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)) == 24000 &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34, 2)) == 16);
        using var providerHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var router = new EllaiReasoningRouter(options, new EllaiReasoningAdapter(providerHttp, options),
            new OllamaEllaiReasoningProvider(providerHttp, options), new ServerlessEllaiReasoningProvider(providerHttp, options));
        var sessions = new InMemoryEllaiSessionStore();
        await sessions.CreateAsync(new EllaiSessionRecord("session", EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 10, "owner", "scope", null, null, DateTimeOffset.UtcNow), default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IEllaiSessionStore>(sessions);
        builder.Services.AddSingleton<EllaiConsentService>();
        builder.Services.AddSingleton<IEllaiConsentStore, InMemoryEllaiConsentStore>();
        builder.Services.AddSingleton<EllaiUsageLedger>();
        builder.Services.AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        builder.Services.AddSingleton<IEllaiTurnCompletionClient>(new EllaiProviderTurnClient(router));
        builder.Services.AddSingleton(new MetaAsrUploadAdapter(providerHttp, options));
        builder.Services.AddSingleton(new MetaAsrRealtimeAdapter(options));
        builder.Services.AddSingleton(new EllaiVoiceTurnCoordinator());
        builder.Services.AddSingleton<IVoiceActivityDetector, SileroOnnxVoiceActivityDetector>();
        builder.Services.AddTransient<SileroVoiceActivityAudioStream>();
        builder.Services.AddScoped<ReportVoiceExtraction>();
        builder.Services.AddScoped<ReportVoiceExtractionAdmission>();
        builder.Services.AddScoped<ReportVoiceGatewayService>();
        builder.Services.AddScoped<EllaiVoiceTranscriptionService>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var group = app.MapGroup("/sessions/{sessionId}").RequireAuthorization();
        var consentHandler = ReportVoiceEndpoints.ConsentAsync;
        group.MapPost("/consent", consentHandler);
        group.MapPost("/checked-consent", consentHandler).AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Items[EllaiGuestEndpoints.ValidatedAntiforgeryRequest] = true;
            return await next(context);
        });
        var extractHandler = ReportVoiceEndpoints.ExtractAsync;
        group.MapPost("/extract", extractHandler);
        var savedHandler = ReportVoiceEndpoints.TranscribeSavedAsync;
        group.MapPost("/saved", savedHandler);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var checkedRequest = new HttpRequestMessage(HttpMethod.Post, "/sessions/session/checked-consent")
        { Content = JsonContent.Create(new ReportVoiceConsentRequest(true)) };
        checkedRequest.Headers.Add("Cookie", "synthetic=1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(checkedRequest)).StatusCode);
        using var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        using var response = await client.PostAsync("/sessions/session/saved", audio);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transcription = await response.Content.ReadFromJsonAsync<EllaiVoiceTranscriptionResult>();
        Assert.NotNull(transcription);
        Assert.False(string.IsNullOrWhiteSpace(transcription.Transcript));
        Assert.False(transcription.ServerSubmission);
        using var extracted = await client.PostAsJsonAsync("/sessions/session/extract", new ReportVoiceExtractionRequest(transcription.Transcript));
        Assert.Equal(HttpStatusCode.OK, extracted.StatusCode);
        Assert.Equal("no-store, private", extracted.Headers.CacheControl!.ToString());
        var report = await extracted.Content.ReadFromJsonAsync<ReportVoiceExtractionResponse>();
        Assert.Equal("Ready", report!.Status);
        Assert.Contains("age", report.Suggestions.Select(item => item.Field));
        Assert.Contains("locality", report.Suggestions.Select(item => item.Field));
        Assert.All(report.Suggestions, item => Assert.True(transcription.Transcript.Contains(item.Evidence, StringComparison.Ordinal)));
        Assert.True((await app.Services.GetRequiredService<IEllaiUsageStore>().TotalsAsync("session", default)).TotalTokens > 0);

        using var scope = app.Services.CreateScope();
        var voice = scope.ServiceProvider.GetRequiredService<EllaiVoiceTranscriptionService>();
        Assert.True(await voice.GrantVoiceConsentAsync("session", default));
        var pcm = new byte[wav.Length - 44 + 48_000];
        Array.Copy(wav, 44, pcm, 0, wav.Length - 44);
        using var liveAudio = new OpenPcmStream(pcm);
        using var liveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var liveTask = voice.TranscribeAsync("session", EllaiVoiceCaptureMode.Live, liveAudio,
            null, liveTimeout.Token, "synthetic-live-capture", submissionPrincipal: null);
        var coordinator = app.Services.GetRequiredService<EllaiVoiceTurnCoordinator>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        try
        {
            while (coordinator.GetCompletedTurns("session").Count == 0 && !liveTask.IsCompleted && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(100, liveTimeout.Token);
            var completed = coordinator.GetCompletedTurns("session");
            Assert.NotEmpty(completed);
            Assert.False(liveTask.IsCompleted);
            using var liveExtracted = await client.PostAsJsonAsync("/sessions/session/extract",
                new ReportVoiceExtractionRequest(completed[0].Transcript));
            Assert.Equal(HttpStatusCode.OK, liveExtracted.StatusCode);
            var liveReport = await liveExtracted.Content.ReadFromJsonAsync<ReportVoiceExtractionResponse>();
            Assert.Equal("Ready", liveReport!.Status);
            Assert.NotEmpty(liveReport.Suggestions);
            Assert.False(liveTask.IsCompleted);
            Assert.Empty(await sessions.ReadConversationAsync("session", "owner", default));
        }
        finally { liveAudio.Complete(); }
        var live = await liveTask;
        Assert.NotNull(live);
        Assert.False(live.ServerSubmission);
        Assert.False(string.IsNullOrWhiteSpace(live.Transcript));
    }

    private sealed class OpenPcmStream(byte[] pcm) : Stream
    {
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;
        public void Complete() => _end.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_end.Task.IsCompleted) return 0;
            await Task.Delay(100, cancellationToken);
            if (_end.Task.IsCompleted) return 0;
            var count = Math.Min(buffer.Length, 4_800);
            if (_offset < pcm.Length)
            {
                count = Math.Min(count, pcm.Length - _offset);
                pcm.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
            }
            else
            {
                buffer.Span[..count].Clear();
            }
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SyntheticGatewayLiveFactAttribute : FactAttribute
    {
        public SyntheticGatewayLiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_SYNTHETIC_REPORT_WAV")))
                Skip = "Set ELLAI_LIVE=1 and SWIFTAID_TEST_SYNTHETIC_REPORT_WAV to synthetic speech.";
        }
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
