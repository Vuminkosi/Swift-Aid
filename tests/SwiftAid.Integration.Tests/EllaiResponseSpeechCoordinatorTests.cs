using SwiftAid.Gateway.Ellai;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Endpoints;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Headers;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiResponseSpeechCoordinatorTests
{
    [Fact]
    public async Task FastFinalResponsePreventsObsoleteAcknowledgementSynthesis()
    {
        var responses = new EllaiSessionResponseStore();
        var progress = new EllaiToolProgressPublisher(responses, "fast", "turn", 1, 1);
        progress.Observe(new("fast", 1, 1, EllaiActivityKind.ToolStarted, "cycle", "read",
            DateTimeOffset.UtcNow, EllaiToolNames.Now));
        var ack = Assert.Single(responses.ReadSince("fast", 0).Responses);
        responses.Reconcile("fast", "turn", EllaiUtteranceResolution.Completed("It is ready.", 1));
        var speech = new SentenceSpeechSynthesisClient();
        var result = await EllaiGuestEndpoints.StreamResponseAudioAsync("fast", ack.Sequence,
            new DefaultHttpContext(), responses, speech, new(), new(), Guid.NewGuid());
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(speech.Texts);
    }

    [Fact]
    public async Task Interrupt_CancelsOnlyTheMatchingPlayback()
    {
        var coordinator = new EllaiResponseSpeechCoordinator();
        var playbackId = Guid.NewGuid();
        using var matching = coordinator.Begin("session-1", 4, playbackId, CancellationToken.None);
        using var otherSequence = coordinator.Begin("session-1", 5, playbackId, CancellationToken.None);
        using var otherSession = coordinator.Begin("session-2", 4, playbackId, CancellationToken.None);

        Assert.True(coordinator.Interrupt("session-1", 4, playbackId));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Task.Delay(Timeout.InfiniteTimeSpan, matching.Token));
        Assert.False(otherSequence.Token.IsCancellationRequested);
        Assert.False(otherSession.Token.IsCancellationRequested);
    }

    [Fact]
    public void InterruptBeforeStreamRegistration_FencesTheLatePlayback()
    {
        var coordinator = new EllaiResponseSpeechCoordinator();
        var playbackId = Guid.NewGuid();

        Assert.False(coordinator.Interrupt("session-1", 4, playbackId));
        using var latePlayback = coordinator.Begin("session-1", 4, playbackId, CancellationToken.None);

        Assert.True(latePlayback.Token.IsCancellationRequested);
    }

    [Fact]
    public void RepeatedInterrupt_IsIdempotentAndDoesNotCancelAnotherPlayback()
    {
        var coordinator = new EllaiResponseSpeechCoordinator();
        var playbackId = Guid.NewGuid();
        using var first = coordinator.Begin("session-1", 4, playbackId, CancellationToken.None);
        using var repeated = coordinator.Begin("session-1", 4, playbackId, CancellationToken.None);

        Assert.True(coordinator.Interrupt("session-1", 4, playbackId));
        Assert.True(coordinator.Interrupt("session-1", 4, playbackId));
        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(repeated.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ResponseStreamInterrupt_CancelsCopyAndDisposesOnlyTheSpeechStream()
    {
        const string sessionId = "session-1";
        const long sequence = 1;
        var playbackId = Guid.NewGuid();
        var responses = new EllaiSessionResponseStore();
        responses.Publish(sessionId, "turn-1", "A response");
        var speechStream = new BlockingReadStream();
        var speech = new BlockingSpeechSynthesisClient(speechStream);
        var coordinator = new EllaiResponseSpeechCoordinator();
        var turnCoordinator = new EllaiVoiceTurnCoordinator();
        var output = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Response.Body = output;

        var result = await EllaiGuestEndpoints.StreamResponseAudioAsync(
            sessionId, sequence, http, responses, speech, coordinator, turnCoordinator, playbackId);
        var execution = result.ExecuteAsync(http);
        await speechStream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var interruption = EllaiGuestEndpoints.InterruptResponseAudioAsync(
            sessionId, sequence, playbackId, responses, coordinator, turnCoordinator);
        await execution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StatusCodes.Status204NoContent, Assert.IsAssignableFrom<IStatusCodeHttpResult>(interruption).StatusCode);
        Assert.True(speech.SynthesisToken.IsCancellationRequested);
        Assert.Equal(0, output.Length);
        Assert.Equal(EllaiVoiceTurnState.Interrupted, turnCoordinator.GetSnapshot(sessionId).State);
    }

    [Fact]
    public async Task ResponseAudioSynthesizesSentenceChunksSequentiallyIntoOnePcmStream()
    {
        const string sessionId = "session-sentence-stream";
        const long sequence = 1;
        var playbackId = Guid.NewGuid();
        var responses = new EllaiSessionResponseStore();
        responses.Publish(sessionId, "turn-sentences", "First sentence. Second sentence!");
        var speech = new SentenceSpeechSynthesisClient();
        var playbackCoordinator = new EllaiResponseSpeechCoordinator();
        var turnCoordinator = new EllaiVoiceTurnCoordinator();
        var http = new DefaultHttpContext();
        using var output = new MemoryStream();
        http.Response.Body = output;

        var result = await EllaiGuestEndpoints.StreamResponseAudioAsync(
            sessionId, sequence, http, responses, speech, playbackCoordinator, turnCoordinator, playbackId);
        Assert.Equal(["First sentence."], speech.Texts);

        await result.ExecuteAsync(http);

        Assert.Equal(["First sentence.", "Second sentence!"], speech.Texts);
        Assert.Equal(["session-sentence-stream-1-0", "session-sentence-stream-1-1"], speech.RequestIds);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ToArray());
    }

    [Fact]
    public async Task ClientBargeInCandidateLeavesServerSpeechRunningUntilGatewayVadConfirms()
    {
        const string sessionId = "session-client-candidate";
        const long sequence = 1;
        var playbackId = Guid.NewGuid();
        var interruptionTurnId = Guid.NewGuid();
        var responses = new EllaiSessionResponseStore();
        responses.Publish(sessionId, "turn-3", "Assigned cases");
        var speechStream = new BlockingReadStream();
        var speech = new BlockingSpeechSynthesisClient(speechStream);
        var speechCoordinator = new EllaiResponseSpeechCoordinator();
        var turnCoordinator = new EllaiVoiceTurnCoordinator();
        var voiceStream = turnCoordinator.BeginStream(sessionId, CancellationToken.None);
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        var audio = await EllaiGuestEndpoints.StreamResponseAudioAsync(
            sessionId, sequence, http, responses, speech, speechCoordinator, turnCoordinator, playbackId);
        var execution = audio.ExecuteAsync(http);
        await speechStream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var candidate = EllaiGuestEndpoints.InterruptResponseAudioAsync(
            sessionId, sequence, playbackId, responses, speechCoordinator, turnCoordinator,
            interruptionTurnId.ToString("N"), voiceStream.Epoch);

        Assert.Equal(StatusCodes.Status204NoContent,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(candidate).StatusCode);
        Assert.False(speech.SynthesisToken.IsCancellationRequested);
        Assert.Equal(EllaiVoiceTurnState.AssistantSpeaking, turnCoordinator.GetSnapshot(sessionId).State);

        EllaiBargeInDecision? confirmed = null;
        for (var sequenceNumber = 0; sequenceNumber < 6; sequenceNumber++)
        {
            confirmed = turnCoordinator.ObserveAcousticFrame(
                voiceStream,
                new VoiceActivityResult(sequenceNumber, 0.9f, true, false),
                TimeSpan.FromMilliseconds(32));
        }

        Assert.NotNull(confirmed);
        Assert.True(speechCoordinator.Interrupt(
            confirmed.SessionId, confirmed.ResponseSequence, confirmed.PlaybackId));
        await execution.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(speech.SynthesisToken.IsCancellationRequested);
        Assert.Equal(EllaiVoiceTurnState.UserSpeaking, turnCoordinator.GetSnapshot(sessionId).State);
    }

    private sealed class BlockingSpeechSynthesisClient(BlockingReadStream stream) : IEllaiSpeechSynthesisClient
    {
        public bool IsConfigured => true;
        public CancellationToken SynthesisToken { get; private set; }

        public Task<HttpResponseMessage> SynthesizeAsync(
            string requestId, string text, CancellationToken cancellationToken)
        {
            SynthesisToken = cancellationToken;
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
            return Task.FromResult(CreateSpeechResponse(content));
        }
    }

    private sealed class SentenceSpeechSynthesisClient : IEllaiSpeechSynthesisClient
    {
        private static readonly byte[][] Audio = [[1, 2], [3, 4]];

        public bool IsConfigured => true;
        public List<string> Texts { get; } = [];
        public List<string> RequestIds { get; } = [];

        public Task<HttpResponseMessage> SynthesizeAsync(
            string requestId, string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestIds.Add(requestId);
            Texts.Add(text);
            var content = new ByteArrayContent(Audio[Texts.Count - 1]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
            return Task.FromResult(CreateSpeechResponse(content));
        }
    }

    private static HttpResponseMessage CreateSpeechResponse(HttpContent content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.Add("X-SwiftAid-Voice-Id", "swiftaid-mms-english-v1");
        response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
        return response;
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
