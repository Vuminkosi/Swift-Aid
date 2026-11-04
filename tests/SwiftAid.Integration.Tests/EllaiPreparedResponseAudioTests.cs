using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiPreparedResponseAudioTests
{
    [Fact]
    public async Task ReconciliationReusesStreamedWorkUntilRecoveryAudioIsRequested()
    {
        var speech = new HeldSpeech();
        using var prepared = Create(speech);
        await prepared.StartAsync(default);
        try
        {
            var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
            var chunk = responses.PublishChunk("session", "turn", 1, "A sentence.")!;
            await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var final = responses.Reconcile("session", "turn", EllaiUtteranceResolution.Completed("A sentence.", 1));
            speech.Release.TrySetResult();
            using (var first = await prepared.OpenAsync("session", chunk.Sequence, default))
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, await first.Content.ReadAsByteArrayAsync());
            Assert.Single(speech.Requests);
            using (var recovered = await prepared.OpenAsync("session", final.Sequence, default))
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, await recovered.Content.ReadAsByteArrayAsync());
            Assert.Equal(2, speech.Requests.Count);
            using var repeated = await prepared.OpenAsync("session", final.Sequence, default);
            Assert.Equal(2, speech.Requests.Count);
        }
        finally { await prepared.StopAsync(default); }
    }

    [Fact]
    public async Task PublicationStartsSynthesisBeforeFinalResponseOrAnyAudioFetchAndReconnectReusesIt()
    {
        var speech = new HeldSpeech();
        using var prepared = Create(speech);
        await prepared.StartAsync(CancellationToken.None);
        try
        {
            var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
            var sentence = responses.PublishChunk("session", "turn", 1, "First sentence.")!;
            await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(sentence.IsFinal);
            Assert.Single(speech.Requests);
            // Repeated publication and retrieval cannot initiate another provider request.
            responses.PublishChunk("session", "turn", 1, "First sentence.");
            speech.Release.TrySetResult();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var http = new DefaultHttpContext();
                using var body = new MemoryStream();
                http.Response.Body = body;
                var result = await EllaiGuestEndpoints.StreamResponseAudioAsync("session", sentence.Sequence,
                    http, responses, prepared, new(), new(), Guid.NewGuid());
                await result.ExecuteAsync(http);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, body.ToArray());
            }
            Assert.Single(speech.Requests);
        }
        finally { await prepared.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task MissingAudioFetchNeverStartsSynthesis()
    {
        var speech = new HeldSpeech();
        using var prepared = Create(speech);
        var responses = new EllaiSessionResponseStore();
        var response = responses.Publish("session", "turn", "Not prepared.")!;
        var result = await EllaiGuestEndpoints.StreamResponseAudioAsync("session", response.Sequence,
            new DefaultHttpContext(), responses, prepared, new(), new(), Guid.NewGuid());
        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(speech.Requests);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("superseded")]
    [InlineData("failed")]
    public async Task InvalidatedPendingAudioIsCancelledAndCannotBeRetrieved(string reason)
    {
        var speech = new HeldSpeech();
        using var prepared = Create(speech);
        await prepared.StartAsync(CancellationToken.None);
        try
        {
            var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
            responses.PublishChunk("session", "turn", 1, "Provisional sentence.");
            await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (reason == "clear") responses.Clear("session");
            else if (reason == "superseded") responses.PublishChunk("session", "next-turn", 1, "New sentence.");
            else responses.Reconcile("session", "turn", EllaiUtteranceResolution.Failed);
            await speech.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.OpenAsync("session", 1, CancellationToken.None));
        }
        finally { await prepared.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DisconnectingAnAudioReaderDoesNotCancelPreparationOrCauseDuplicateSynthesis()
    {
        var speech = new HeldSpeech();
        using var prepared = Create(speech);
        await prepared.StartAsync(CancellationToken.None);
        try
        {
            var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
            responses.PublishChunk("session", "turn", 1, "A sentence.");
            await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var disconnect = new CancellationTokenSource();
            var read = prepared.OpenAsync("session", 1, disconnect.Token);
            disconnect.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.False(speech.Cancelled.Task.IsCompleted);
            speech.Release.TrySetResult();
            using var retry = await prepared.OpenAsync("session", 1, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await retry.Content.ReadAsByteArrayAsync());
            Assert.Single(speech.Requests);
        }
        finally { await prepared.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task HttpClientReceivesPreparedPcmThroughGatewayHandler()
    {
        var speech = new HeldSpeech();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IEllaiSpeechSynthesisClient>(speech);
        builder.Services.AddSingleton<EllaiPreparedResponseAudio>();
        builder.Services.AddSingleton<IEllaiResponseAudioPreparation>(services => services.GetRequiredService<EllaiPreparedResponseAudio>());
        builder.Services.AddHostedService(services => services.GetRequiredService<EllaiPreparedResponseAudio>());
        builder.Services.AddSingleton<EllaiSessionResponseStore>();
        builder.Services.AddSingleton<EllaiResponseSpeechCoordinator>();
        builder.Services.AddSingleton<EllaiVoiceTurnCoordinator>();
        await using var app = builder.Build();
        Delegate handler = (Func<string, long, HttpContext, EllaiSessionResponseStore,
            EllaiPreparedResponseAudio, EllaiResponseSpeechCoordinator, EllaiVoiceTurnCoordinator, Guid?, Task<IResult>>)
            EllaiGuestEndpoints.StreamResponseAudioAsync;
        app.MapGet("/sessions/{sessionId}/responses/{sequence}/audio", handler);
        await app.StartAsync();
        try
        {
            var responses = app.Services.GetRequiredService<EllaiSessionResponseStore>();
            responses.PublishChunk("http-session", "turn", 1, "Prepared before retrieval.");
            await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            speech.Release.TrySetResult();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var audio = await client.GetAsync("/sessions/http-session/responses/1/audio?playbackId=" + Guid.NewGuid());
            Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
            Assert.Equal("application/vnd.swiftaid.pcm", audio.Content.Headers.ContentType?.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await audio.Content.ReadAsByteArrayAsync());
            Assert.Single(speech.Requests);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task FullPreparationQueueReturnsUnavailableWithoutStartingOnDemandWork()
    {
        var speech = new HeldSpeech();
        using var prepared = Create(speech);
        var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
        // Deliberately do not start the consumer: fill the bounded queue deterministically.
        for (var index = 0; index < 65; index++)
            responses.PublishChunk($"session-{index}", "turn", 1, "Sentence.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.OpenAsync("session-64", 1, CancellationToken.None));
        Assert.Empty(speech.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureOrOversizedAudioNeverProducesSuccessfulTruncatedPcm(bool oversized)
    {
        using var prepared = Create(new InvalidSpeech(oversized));
        await prepared.StartAsync(CancellationToken.None);
        try
        {
            var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
            responses.PublishChunk("failed", "turn", 1, "Sentence.");
            var error = await Record.ExceptionAsync(async () =>
            {
                using var audio = await prepared.OpenAsync("failed", 1, CancellationToken.None);
                await audio.Content.ReadAsByteArrayAsync();
            });
            Assert.NotNull(error);
            Assert.True(error is OperationCanceledException or InvalidOperationException or HttpRequestException or IOException);
        }
        finally { await prepared.StopAsync(CancellationToken.None); }
    }

    private sealed class InvalidSpeech(bool oversized) : IEllaiSpeechSynthesisClient
    {
        public bool IsConfigured => true;
        public Task<HttpResponseMessage> SynthesizeAsync(string requestId, string text, CancellationToken cancellationToken)
        {
            if (!oversized) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 2]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Voice-Id", "test");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            return Task.FromResult(response);
        }
    }

    private static EllaiPreparedResponseAudio Create(IEllaiSpeechSynthesisClient speech) =>
        new(speech, NullLogger<EllaiPreparedResponseAudio>.Instance);

    private sealed class HeldSpeech : IEllaiSpeechSynthesisClient
    {
        public bool IsConfigured => true;
        public ConcurrentQueue<string> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HttpResponseMessage> SynthesizeAsync(string requestId, string text, CancellationToken cancellationToken)
        {
            Requests.Enqueue(requestId);
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Voice-Id", "test");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            return response;
        }
    }
}
