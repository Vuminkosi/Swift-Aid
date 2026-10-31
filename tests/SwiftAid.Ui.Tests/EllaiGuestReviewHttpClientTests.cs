using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
public sealed class EllaiGuestReviewHttpClientTests
{
    [Fact]
    public async Task PublicHelpConsentUsesGuestRouteAndAntiforgeryHeader()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);

        Assert.True(await client.GrantPublicProductHelpConsentAsync("guest-session", CancellationToken.None));
        Assert.Equal(HttpMethod.Get, handler.Methods[0]);
        Assert.Equal("/api/v1/ellai/guest/antiforgery", handler.Paths[0]);
        Assert.Equal(HttpMethod.Post, handler.Methods[1]);
        Assert.Equal("/api/v1/ellai/guest/sessions/guest-session/consents", handler.Paths[1]);
        Assert.Equal("csrf-test", handler.CsrfHeaders[1]);
        Assert.Contains("ellai-public-product-help", handler.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseAudioUsesGuestGatewayRouteAndValidatesPcmIdentity()
    {
        var handler = new AudioHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);

        var audio = await client.DownloadResponseAudioAsync("guest-session", 7, CancellationToken.None);

        Assert.Equal("/api/v1/ellai/guest/sessions/guest-session/responses/7/audio", handler.Path);
        Assert.NotNull(audio);
        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal("swiftaid-mms-english-v1", audio.VoiceId);
        Assert.Equal(7, audio.Sequence);
        Assert.Equal(new byte[] { 0x01, 0x00, 0xFF, 0x7F }, audio.Pcm);
    }

    [Fact]
    public async Task ListeningGreetingUsesSessionScopedGuestRouteAndValidatesPurpose()
    {
        var handler = new GreetingAudioHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);

        var audio = await client.DownloadListeningGreetingAudioAsync("guest-session", CancellationToken.None);

        Assert.Equal("/api/v1/ellai/guest/sessions/guest-session/voice/greeting/audio", handler.Path);
        Assert.NotNull(audio);
        Assert.Equal(0, audio.Sequence);
        Assert.Equal("swiftaid-mms-english-v1", audio.VoiceId);
        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal(new byte[] { 0x01, 0x00 }, audio.Pcm);
    }

    [Fact]
    public async Task VoiceConsentGrantsMicrophoneAndTranscriptionSeparatelyOnGuestRoute()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);

        Assert.True(await client.GrantVoiceCaptureConsentAsync("guest-session", CancellationToken.None));

        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Post }, handler.Methods);
        Assert.All(handler.Paths.Skip(1), path =>
            Assert.Equal("/api/v1/ellai/guest/sessions/guest-session/consents", path));
        Assert.Equal("csrf-test", handler.CsrfHeaders[1]);
        Assert.Equal("csrf-test", handler.CsrfHeaders[2]);
        Assert.Contains("Microphone", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("Transcription", handler.Bodies[2], StringComparison.Ordinal);
        Assert.All(handler.Bodies.Skip(1), body =>
            Assert.Contains(EllaiConsentPurposes.VoiceTranscription, body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordedVoiceTranscriptionUsesGatewayGuestRouteAndWaveContentType()
    {
        var handler = new TranscriptionHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);

        var transcript = await client.TranscribeVoiceAsync(
            "guest-session", [1, 2, 3, 4], EllaiVoiceCaptureMode.Recorded, CancellationToken.None);

        Assert.Equal("heard words", transcript?.Transcript);
        Assert.Equal("/api/v1/ellai/guest/sessions/guest-session/voice/recorded", handler.PostPath);
        Assert.Equal("audio/wav", handler.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, handler.AudioBytes);
        Assert.Equal("csrf-test", handler.CsrfToken);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public List<string?> Paths { get; } = [];
        public List<string?> CsrfHeaders { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Paths.Add(request.RequestUri?.PathAndQuery);
            CsrfHeaders.Add(request.Headers.TryGetValues("X-CSRF-TOKEN", out var values)
                ? values.Single()
                : null);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var content = request.RequestUri?.AbsolutePath.EndsWith("/antiforgery", StringComparison.Ordinal) == true
                ? "{\"requestToken\":\"csrf-test\"}"
                : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class AudioHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.PathAndQuery;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x01, 0x00, 0xFF, 0x7F]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Session-Id", "guest-session");
            response.Headers.Add("X-SwiftAid-Response-Sequence", "7");
            response.Headers.Add("X-SwiftAid-Sample-Format", "s16le");
            response.Headers.Add("X-SwiftAid-Channels", "1");
            response.Headers.Add("X-SwiftAid-Voice-Id", "swiftaid-mms-english-v1");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            return Task.FromResult(response);
        }
    }

    private sealed class GreetingAudioHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.PathAndQuery;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x01, 0x00]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Session-Id", "guest-session");
            response.Headers.Add("X-SwiftAid-Response-Sequence", "0");
            response.Headers.Add("X-SwiftAid-Speech-Purpose", "live-activation");
            response.Headers.Add("X-SwiftAid-Sample-Format", "s16le");
            response.Headers.Add("X-SwiftAid-Channels", "1");
            response.Headers.Add("X-SwiftAid-Voice-Id", "swiftaid-mms-english-v1");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            return Task.FromResult(response);
        }
    }

    private sealed class TranscriptionHandler : HttpMessageHandler
    {
        public string? PostPath { get; private set; }
        public string? ContentType { get; private set; }
        public string? CsrfToken { get; private set; }
        public byte[]? AudioBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/antiforgery", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"requestToken\":\"csrf-test\"}", Encoding.UTF8, "application/json"),
                };
            }

            PostPath = request.RequestUri?.AbsolutePath;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            CsrfToken = request.Headers.TryGetValues("X-CSRF-TOKEN", out var values) ? values.Single() : null;
            AudioBytes = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"transcript\":\"heard words\"}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
