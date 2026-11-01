using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
public sealed class EllaiGuestReviewHttpClientTests
{
    [Theory]
    [InlineData("http://localhost:8080/", "ws://localhost:8080/api/v1/ellai/guest/sessions/guest-session/voice/live-stream?voiceCaptureId=capture-1&serverSubmission=true")]
    [InlineData("https://example.test/app/", "wss://example.test/app/api/v1/ellai/guest/sessions/guest-session/voice/live-stream?voiceCaptureId=capture-1&serverSubmission=true")]
    public void LiveVoiceWebSocketUsesSameOriginAndSecureScheme(
        string baseAddress, string expected)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);

        var uri = client.CreateLiveVoiceWebSocketUri(
            new Uri(baseAddress), "guest-session", "capture-1");

        Assert.Equal(expected, uri.ToString());
    }

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
    public async Task ResponsePlaybackCarriesPlaybackIdentityAndInterruptUsesGuestAntiforgery()
    {
        var playbackId = Guid.NewGuid().ToString("N");
        var audioHandler = new AudioHandler();
        using var audioHttp = new HttpClient(audioHandler) { BaseAddress = new Uri("https://localhost/") };
        var audioClient = new EllaiReviewHttpClient(audioHttp, useGuestRoutes: true);
        var audio = await audioClient.DownloadResponseAudioAsync(
            "guest-session", 7, playbackId, CancellationToken.None);

        Assert.NotNull(audio);
        Assert.Equal($"/api/v1/ellai/guest/sessions/guest-session/responses/7/audio?playbackId={playbackId}",
            audioHandler.Path);

        var interruptHandler = new RecordingHandler();
        using var interruptHttp = new HttpClient(interruptHandler) { BaseAddress = new Uri("https://localhost/") };
        var interruptClient = new EllaiReviewHttpClient(interruptHttp, useGuestRoutes: true);
        Assert.True(await interruptClient.InterruptResponseAudioAsync(
            "guest-session", 7, playbackId, CancellationToken.None));

        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Post }, interruptHandler.Methods);
        Assert.Equal($"/api/v1/ellai/guest/sessions/guest-session/responses/7/audio/{playbackId}/interrupt",
            interruptHandler.Paths[1]);
        Assert.Equal("csrf-test", interruptHandler.CsrfHeaders[1]);
    }

    [Fact]
    public async Task ResponseAudioStreamReturnsBeforeReadingPcmBody()
    {
        var body = new TrackingReadStream([0x01, 0x00, 0xFF, 0x7F]);
        var handler = new AudioHandler(body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);

        await using var audio = await client.DownloadResponseAudioStreamAsync(
            "guest-session", 7, Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.NotNull(audio);
        Assert.Equal(0, body.BytesRead);
        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal("swiftaid-mms-english-v1", audio.VoiceId);
        Assert.Equal(7, audio.Sequence);
        using var buffered = new MemoryStream();
        await audio.Pcm.CopyToAsync(buffered);
        Assert.Equal(new byte[] { 0x01, 0x00, 0xFF, 0x7F }, buffered.ToArray());
        Assert.True(body.BytesRead >= 4);
        await audio.DisposeAsync();
        Assert.True(body.IsDisposed);
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
        Assert.Equal("/api/v1/ellai/guest/sessions/guest-session/voice/recorded?serverSubmission=true", handler.PostPath);
        Assert.Equal("audio/wav", handler.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, handler.AudioBytes);
        Assert.Equal("csrf-test", handler.CsrfToken);
    }

    [Fact]
    public async Task BufferedLiveVoiceRecoveryUsesExistingLiveRouteAndRawPcmContentType()
    {
        var handler = new TranscriptionHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);

        var transcript = await client.TranscribeVoiceAsync(
            "guest-session", [1, 2, 3, 4], EllaiVoiceCaptureMode.Live, CancellationToken.None);

        Assert.Equal("heard words", transcript?.Transcript);
        Assert.Equal("/api/v1/ellai/guest/sessions/guest-session/voice/live?serverSubmission=true", handler.PostPath);
        Assert.Equal("application/octet-stream", handler.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, handler.AudioBytes);
        Assert.Equal("csrf-test", handler.CsrfToken);
    }

    [Fact]
    public async Task LiveVoiceUsesStreamingGuestRequestOnExistingSessionRoute()
    {
        var handler = new LiveTranscriptionHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: true);
        using var audio = new NonSeekableStream([1, 2, 3, 4, 5, 6]);

        var result = await client.TranscribeLiveVoiceStreamAsync(
            "guest-session", audio, CancellationToken.None, "voice-capture-1", 4_800);

        Assert.Equal("streamed words", result?.Transcript);
        Assert.Equal(
            "/api/v1/ellai/guest/sessions/guest-session/voice/live?serverSubmission=true&voiceCaptureId=voice-capture-1&voiceCaptureOffsetSamples=4800",
            handler.Path);
        Assert.Equal("application/octet-stream", handler.ContentType);
        Assert.Null(handler.ContentLength);
        Assert.Equal("csrf-test", handler.CsrfToken);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, handler.AudioBytes);
    }

    [Theory]
    [InlineData(true, "/api/v1/ellai/guest/sessions/guest-session/voice/state")]
    [InlineData(false, "/api/v1/ellai/sessions/guest-session/voice/state")]
    public async Task VoiceTurnStatusUsesSessionScopedReadRouteWithoutTranscriptData(
        bool guestRoute, string expectedPath)
    {
        var handler = new VoiceStatusHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var client = new EllaiReviewHttpClient(http, useGuestRoutes: guestRoute);

        var status = await client.GetVoiceTurnStatusAsync("guest-session", CancellationToken.None);

        Assert.Equal(expectedPath, handler.Path);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.NotNull(status);
        Assert.Equal("UserSpeaking", status.State);
        Assert.Equal(5, status.Epoch);
        Assert.Equal("temporary words", status.PartialTranscript);
        Assert.Null(handler.CsrfToken);
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
        private readonly Stream? _pcmStream;

        public AudioHandler(Stream? pcmStream = null) => _pcmStream = pcmStream;

        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.PathAndQuery;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = _pcmStream is null
                    ? new ByteArrayContent([0x01, 0x00, 0xFF, 0x7F])
                    : new StreamContent(_pcmStream),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Session-Id", "guest-session");
            response.Headers.Add("X-SwiftAid-Response-Sequence", "7");
            response.Headers.Add("X-SwiftAid-Sample-Format", "s16le");
            response.Headers.Add("X-SwiftAid-Channels", "1");
            response.Headers.Add("X-SwiftAid-Voice-Id", "swiftaid-mms-english-v1");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            var playbackId = request.RequestUri?.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0] == "playbackId")
                .Select(parts => Uri.UnescapeDataString(parts[1]))
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(playbackId))
            {
                response.Headers.Add("X-SwiftAid-Playback-Id", playbackId);
            }
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }

        public bool IsDisposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = Read(buffer, offset, count);
            return Task.FromResult(read);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
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

            PostPath = request.RequestUri?.PathAndQuery;
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

    private sealed class LiveTranscriptionHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? ContentType { get; private set; }
        public long? ContentLength { get; private set; }
        public string? CsrfToken { get; private set; }
        public byte[] AudioBytes { get; private set; } = [];

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

            Path = request.RequestUri?.PathAndQuery;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            ContentLength = request.Content?.Headers.ContentLength;
            CsrfToken = request.Headers.TryGetValues("X-CSRF-TOKEN", out var values)
                ? values.Single()
                : null;
            AudioBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"transcript\":\"streamed words\"}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class VoiceStatusHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? CsrfToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Method = request.Method;
            CsrfToken = request.Headers.TryGetValues("X-CSRF-TOKEN", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"state\":\"UserSpeaking\",\"epoch\":5,\"confirmedSpeechMilliseconds\":320,\"silenceMilliseconds\":0,\"partialTranscript\":\"temporary words\"}",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
