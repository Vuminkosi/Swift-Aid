using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
public sealed class EllaiVoiceTurnCoordinatorTests
{
    private static readonly TimeSpan VadFrameDuration = TimeSpan.FromMilliseconds(32);

    [Fact]
    public void AcousticEndpointIsOnlyACandidateUntilProviderFinalArrives()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        using var stream = coordinator.BeginStream("session-1", CancellationToken.None);

        for (var sequence = 0; sequence < 4; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Speech(sequence), VadFrameDuration);
        }
        Assert.Equal(EllaiVoiceTurnState.UserSpeaking, coordinator.GetSnapshot("session-1").State);

        for (var sequence = 4; sequence < 26; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Silence(sequence), VadFrameDuration);
        }
        Assert.Equal(EllaiVoiceTurnState.EndpointCandidate, coordinator.GetSnapshot("session-1").State);

        Assert.Null(coordinator.AcceptFinalTranscript(stream, "turn-7", ""));
        var final = coordinator.AcceptFinalTranscript(stream, "turn-7", "  The animal needs assistance.  ");

        Assert.Equal("The animal needs assistance.", final?.Text);
        Assert.Equal(EllaiVoiceTurnState.Processing, coordinator.GetSnapshot("session-1").State);
    }

    [Fact]
    public void BriefAcousticNoiseDoesNotConfirmSpeechOrCreateAnEndpoint()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        using var stream = coordinator.BeginStream("session-1", CancellationToken.None);

        coordinator.ObserveAcousticFrame(stream, Speech(0), VadFrameDuration);
        coordinator.ObserveAcousticFrame(stream, Silence(1), VadFrameDuration);

        Assert.Equal(EllaiVoiceTurnState.Listening, coordinator.GetSnapshot("session-1").State);
        Assert.Null(coordinator.AcceptFinalTranscript(stream, "", "not identified"));
    }

    [Fact]
    public void ConfiguredSpeechStartAndSilenceThresholdsDriveStateTransitions()
    {
        var coordinator = new EllaiVoiceTurnCoordinator(new EllaiVoiceTurnOptions(
            SpeechStartConfirmationMs: 64,
            EndOfTurnSilenceMs: 100,
            BargeInConfirmationMs: 96));
        using var stream = coordinator.BeginStream("session-configured", CancellationToken.None);

        coordinator.ObserveAcousticFrame(stream, Speech(0), VadFrameDuration);
        coordinator.ObserveAcousticFrame(stream, Speech(1), VadFrameDuration);
        Assert.Equal(EllaiVoiceTurnState.UserSpeaking, coordinator.GetSnapshot("session-configured").State);

        coordinator.ObserveAcousticFrame(stream, Silence(2), VadFrameDuration);
        coordinator.ObserveAcousticFrame(stream, Silence(3), VadFrameDuration);
        coordinator.ObserveAcousticFrame(stream, Silence(4), VadFrameDuration);
        coordinator.ObserveAcousticFrame(stream, Silence(5), VadFrameDuration);

        Assert.Equal(EllaiVoiceTurnState.EndpointCandidate, coordinator.GetSnapshot("session-configured").State);
    }

    [Theory]
    [InlineData(0, 650, 180)]
    [InlineData(120, 99, 180)]
    [InlineData(120, 650, 2001)]
    public void InvalidThresholdsAreRejected(int speechStartMs, int silenceMs, int bargeInMs)
    {
        var options = new EllaiVoiceTurnOptions(speechStartMs, silenceMs, bargeInMs);

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void NewStreamCancelsPriorEpochAndRejectsItsLateFinal()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        using var first = coordinator.BeginStream("session-1", CancellationToken.None);
        using var second = coordinator.BeginStream("session-1", CancellationToken.None);

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.Null(coordinator.AcceptFinalTranscript(first, "old-turn", "stale words"));
        Assert.NotNull(coordinator.AcceptFinalTranscript(second, "new-turn", "current words"));
        Assert.Equal(first.Epoch + 1, second.Epoch);
    }

    [Fact]
    public void DuplicateFinalsAndOtherSessionsAreIsolated()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        using var first = coordinator.BeginStream("session-1", CancellationToken.None);
        using var other = coordinator.BeginStream("session-2", CancellationToken.None);

        Assert.NotNull(coordinator.AcceptFinalTranscript(first, "turn-1", "heard once"));
        Assert.Null(coordinator.AcceptFinalTranscript(first, "turn-1", "duplicate"));
        Assert.NotNull(coordinator.AcceptFinalTranscript(other, "turn-1", "separate session"));
        Assert.Equal(EllaiVoiceTurnState.Processing, coordinator.GetSnapshot("session-1").State);
        Assert.Equal(EllaiVoiceTurnState.Processing, coordinator.GetSnapshot("session-2").State);
    }

    [Fact]
    public void BargeInRequiresConfirmedSpeechAndCancelsOnlyCurrentPlaybackIdentity()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        const string sessionId = "session-1";
        const long responseSequence = 9;
        var playbackId = Guid.NewGuid();
        coordinator.SetAssistantPlayback(sessionId, responseSequence, playbackId, isPlaying: true);
        using var stream = coordinator.BeginStream(sessionId, CancellationToken.None);

        EllaiBargeInDecision? decision = null;
        for (var sequence = 0; sequence < 5; sequence++)
        {
            decision = coordinator.ObserveAcousticFrame(stream, Speech(sequence), VadFrameDuration);
        }

        Assert.Null(decision);
        Assert.False(coordinator.InterruptPlayback(sessionId, responseSequence, Guid.NewGuid()));

        decision = coordinator.ObserveAcousticFrame(stream, Speech(5), VadFrameDuration);

        Assert.Equal(responseSequence, decision?.ResponseSequence);
        Assert.Equal(playbackId, decision?.PlaybackId);
        Assert.Equal(EllaiVoiceTurnState.UserSpeaking, coordinator.GetSnapshot(sessionId).State);
        Assert.Null(coordinator.SetAssistantPlayback(sessionId, responseSequence, playbackId, isPlaying: false)
            ?.ActivePlaybackId);
    }

    private static VoiceActivityResult Speech(long sequence) => new(sequence, 0.9f, true, false);
    private static VoiceActivityResult Silence(long sequence) => new(sequence, 0.05f, false, false);
}
