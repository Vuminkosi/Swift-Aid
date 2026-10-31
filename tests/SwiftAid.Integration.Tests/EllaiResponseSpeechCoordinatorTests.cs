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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
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
