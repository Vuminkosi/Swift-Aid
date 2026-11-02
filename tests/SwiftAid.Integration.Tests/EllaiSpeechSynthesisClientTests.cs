using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiSpeechSynthesisClientTests
{
    [Fact]
    public async Task CancellingAQueuedSentenceDoesNotCancelOrReleaseAnotherInference()
    {
        using var handler = new SingleInferenceHandler();
        using var http = new HttpClient(handler);
        var client = Create(http);
        var first = client.SynthesizeAsync("playing", "First sentence.", CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var queued = client.SynthesizeAsync("cancelled", "Cancelled sentence.", cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(first.IsCompleted);
            Assert.Equal(0, handler.Rejected);
        }
        finally { handler.Release.TrySetResult(); }
        using var result = await first;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task EveryPublishedSentenceGetsPreparedAudioWhenInferenceIsBusy()
    {
        using var handler = new SingleInferenceHandler();
        using var http = new HttpClient(handler);
        using var prepared = new EllaiPreparedResponseAudio(Create(http), NullLogger<EllaiPreparedResponseAudio>.Instance);
        await prepared.StartAsync(CancellationToken.None);
        try
        {
            var responses = new SwiftAid.Application.Ellai.EllaiSessionResponseStore(audioPreparation: prepared);
            for (var index = 1; index <= 3; index++)
                responses.PublishChunk("multi-sentence", "turn", index, $"Sentence number {index}.");
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            handler.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (var index = 1; index <= 3; index++)
            {
                using var audio = await prepared.OpenAsync("multi-sentence", index, timeout.Token);
                Assert.Equal(new byte[] { 0, 0 }, await audio.Content.ReadAsByteArrayAsync(timeout.Token));
            }
            Assert.Equal(0, handler.Rejected);
        }
        finally { await prepared.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 2, HttpStatusCode.OK, 3)]
    [InlineData(HttpStatusCode.TooManyRequests, 5, HttpStatusCode.TooManyRequests, 3)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 2, HttpStatusCode.ServiceUnavailable, 1)]
    public async Task OnlyBusyRejectionsAreRetriedAndRetriesAreBounded(HttpStatusCode rejectedStatus,
        int rejections, HttpStatusCode expectedStatus, int attempts)
    {
        using var handler = new BusyHandler(rejectedStatus, rejections);
        using var http = new HttpClient(handler);
        using var response = await Create(http).SynthesizeAsync("busy", "Synthetic sentence.", CancellationToken.None);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(attempts, handler.Attempts);
    }

    private sealed class BusyHandler(HttpStatusCode status, int rejections) : HttpMessageHandler
    {
        public int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(++Attempts <= rejections ? status : HttpStatusCode.OK);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ConcurrentSentenceRequestsAcrossClientsRespectSingleInferenceWorker()
    {
        using var handler = new SingleInferenceHandler();
        using var firstHttp = new HttpClient(handler, disposeHandler: false);
        using var secondHttp = new HttpClient(handler, disposeHandler: false);
        var first = Create(firstHttp).SynthesizeAsync("one", "First sentence.", CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = Create(secondHttp).SynthesizeAsync("two", "Second sentence.", CancellationToken.None);
        handler.Release.TrySetResult();
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(0, handler.Rejected);
    }

    private sealed class SingleInferenceHandler : HttpMessageHandler
    {
        private int _active;
        public int Rejected;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            {
                Interlocked.Increment(ref Rejected);
                return new(HttpStatusCode.TooManyRequests);
            }
            try
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0]) };
                response.Content.Headers.ContentType = new("application/vnd.swiftaid.pcm");
                response.Headers.Add("X-SwiftAid-Voice-Id", "test");
                response.Headers.Add("X-SwiftAid-Sample-Rate", "24000");
                return response;
            }
            finally { Interlocked.Exchange(ref _active, 0); }
        }
    }

    [Fact]
    public async Task EagerPreparationUsesTheVoiceCommittedWithTheResponse()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var prepared = new EllaiPreparedResponseAudio(Create(http), NullLogger<EllaiPreparedResponseAudio>.Instance);
        await prepared.StartAsync(CancellationToken.None);
        try
        {
            prepared.Published(new EllaiSessionResponseDto(1, "voice-choice", "Synthetic sentence.",
                DateTimeOffset.UtcNow, "turn", EnglishVoiceId: "am_michael"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var audio = await prepared.OpenAsync("voice-choice", 1, timeout.Token);
            using var request = JsonDocument.Parse(handler.Body!);
            Assert.Equal("am_michael", request.RootElement.GetProperty("voiceId").GetString());
            Assert.Equal("eng", request.RootElement.GetProperty("language").GetString());
        }
        finally { await prepared.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("sna")]
    [InlineData("nya")]
    [InlineData("tso")]
    public async Task PublicationPreparesNativeAudioWithItsOriginalLanguage(string language)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var prepared = new EllaiPreparedResponseAudio(Create(http), NullLogger<EllaiPreparedResponseAudio>.Instance);
        await prepared.StartAsync(CancellationToken.None);
        try
        {
            prepared.Published(new EllaiSessionResponseDto(1, "session", "Synthetic sentence.", DateTimeOffset.UtcNow,
                "turn", Language: language));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var audio = await prepared.OpenAsync("session", 1, timeout.Token);
            Assert.Equal(new byte[] { 0, 0 }, await audio.Content.ReadAsByteArrayAsync(timeout.Token));
            using var request = JsonDocument.Parse(handler.Body!);
            Assert.Equal(language, request.RootElement.GetProperty("language").GetString());
        }
        finally { await prepared.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("eng", "af_heart")]
    [InlineData("eng", "am_michael")]
    [InlineData("eng", null)]
    [InlineData("sna", "default")]
    [InlineData("nya", "default")]
    [InlineData("tso", "default")]
    public async Task SendsExactLanguageAndVoiceWithoutEnglishFallback(string language, string? voice)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = Create(http);
        using var response = await client.SynthesizeAsync("test", "Synthetic sentence.", language, voice, CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(language, body.RootElement.GetProperty("language").GetString());
        Assert.Equal(voice ?? "default", body.RootElement.GetProperty("voiceId").GetString());
        Assert.Equal("Bearer test-key", handler.Authorization);
    }

    [Theory]
    [InlineData("ssw", "default")]
    [InlineData("sna", "af_heart")]
    [InlineData("eng", "uninstalled")]
    public async Task RejectsUnsupportedSelectionsBeforeNetworkCall(string language, string voice)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(http).SynthesizeAsync(
            "test", "Synthetic sentence.", language, voice, CancellationToken.None));
        Assert.Null(handler.Body);
    }

    private static EllaiSpeechSynthesisClient Create(HttpClient http) => new(http,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiTts:Endpoint"] = "http://speech.test/",
            ["EllaiTts:InternalKey"] = "test-key",
        }).Build());

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0]) };
            response.Content.Headers.ContentType = new("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Voice-Id", "test-voice");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            response.Headers.Add("X-SwiftAid-Channels", "1");
            response.Headers.Add("X-SwiftAid-Sample-Format", "s16le");
            return response;
        }
    }
}
