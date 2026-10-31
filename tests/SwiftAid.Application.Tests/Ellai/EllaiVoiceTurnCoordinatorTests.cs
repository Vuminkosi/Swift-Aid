using SwiftAid.Application.Ellai;
using System.Diagnostics.Metrics;

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

        for (var sequence = 4; sequence < 18; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Silence(sequence), VadFrameDuration);
        }
        Assert.Equal(EllaiVoiceTurnState.UserSpeaking, coordinator.GetSnapshot("session-1").State);
        coordinator.ObserveAcousticFrame(stream, Silence(18), VadFrameDuration);
        Assert.Equal(EllaiVoiceTurnState.EndpointCandidate, coordinator.GetSnapshot("session-1").State);

        Assert.Null(coordinator.AcceptFinalTranscript(stream, "turn-7", ""));
        var final = coordinator.AcceptFinalTranscript(stream, "turn-7", "  The animal needs assistance.  ");

        Assert.Equal("The animal needs assistance.", final?.Text);
        Assert.Equal(EllaiVoiceTurnState.TurnCompleted, coordinator.GetSnapshot("session-1").State);
        var completed = Assert.Single(coordinator.GetCompletedTurns("session-1"));
        Assert.Equal("turn-7", completed.ProviderTurnId);
        Assert.Equal("The animal needs assistance.", completed.Transcript);
        Assert.Empty(coordinator.GetCompletedTurns("session-1", afterSequence: completed.Sequence));
    }

    [Fact]
    public void DefaultEndpointCandidateStartsAfterAboutHalfASecondOfSilence()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        using var stream = coordinator.BeginStream("session-fast-endpoint", CancellationToken.None);
        for (var sequence = 0; sequence < 4; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Speech(sequence), VadFrameDuration);
        }

        for (var sequence = 4; sequence < 19; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Silence(sequence), VadFrameDuration);
        }

        Assert.Equal(EllaiVoiceTurnState.EndpointCandidate,
            coordinator.GetSnapshot("session-fast-endpoint").State);
        Assert.Equal(TimeSpan.FromMilliseconds(480),
            coordinator.GetSnapshot("session-fast-endpoint").Silence);
    }

    [Fact]
    public void MuseFinalBeforeSileroEndpointIsNotPublishedUntilSileroConfirmsSilence()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        using var stream = coordinator.BeginStream("session-final-first", CancellationToken.None);
        for (var sequence = 0; sequence < 4; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Speech(sequence), VadFrameDuration);
        }

        Assert.NotNull(stream.AcceptFinalTranscript("muse-turn-8", "Follow up on my report."));
        Assert.Empty(coordinator.GetCompletedTurns("session-final-first"));

        for (var sequence = 4; sequence < 26; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Silence(sequence), VadFrameDuration);
        }

        Assert.Equal(EllaiVoiceTurnState.TurnCompleted,
            coordinator.GetSnapshot("session-final-first").State);
        Assert.Equal("Follow up on my report.",
            Assert.Single(coordinator.GetCompletedTurns("session-final-first")).Transcript);
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
    [InlineData(0, 650, 180, 480_000)]
    [InlineData(120, 99, 180, 480_000)]
    [InlineData(120, 650, 2001, 480_000)]
    [InlineData(120, 650, 180, 999)]
    [InlineData(120, 650, 180, 900_001)]
    public void InvalidThresholdsAreRejected(
        int speechStartMs, int silenceMs, int bargeInMs, int maximumTurnDurationMs)
    {
        var options = new EllaiVoiceTurnOptions(
            speechStartMs, silenceMs, bargeInMs, maximumTurnDurationMs);

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void VoiceTimingMetricsContainNoSessionOrTranscriptDimensions()
    {
        var measurements = new List<string>();
        var nonEmptyTagSets = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SwiftAid.Application.Ellai.Voice")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            measurements.Add(instrument.Name);
            if (tags.Length != 0)
            {
                nonEmptyTagSets++;
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            measurements.Add(instrument.Name);
            if (tags.Length != 0)
            {
                nonEmptyTagSets++;
            }
        });
        listener.Start();

        var coordinator = new EllaiVoiceTurnCoordinator(new EllaiVoiceTurnOptions(64, 100, 96));
        using (var stream = coordinator.BeginStream("private-session-id", CancellationToken.None))
        {
            coordinator.ObserveAcousticFrame(stream, Speech(0), VadFrameDuration);
            coordinator.ObserveAcousticFrame(stream, Speech(1), VadFrameDuration);
            for (var sequence = 2; sequence <= 5; sequence++)
            {
                coordinator.ObserveAcousticFrame(stream, Silence(sequence), VadFrameDuration);
            }
            Assert.NotNull(stream.AcceptFinalTranscript("provider-turn", "private transcript"));
        }

        var playbackId = Guid.NewGuid();
        coordinator.SetAssistantPlayback("private-session-id", 1, playbackId, isPlaying: true);
        using (var stream = coordinator.BeginStream("private-session-id", CancellationToken.None))
        {
            for (var sequence = 0; sequence < 3; sequence++)
            {
                coordinator.ObserveAcousticFrame(stream, Speech(sequence), VadFrameDuration);
            }
        }
        using (var stream = coordinator.BeginStream("private-session-id", CancellationToken.None))
        {
            Assert.True(stream.MarkRecovering());
        }

        Assert.Contains("swiftaid.ellai.voice.speech.start.delay", measurements);
        Assert.Contains("swiftaid.ellai.voice.endpoint.silence", measurements);
        Assert.Contains("swiftaid.ellai.voice.transcription.final.latency", measurements);
        Assert.Contains("swiftaid.ellai.voice.barge_in.confirmation", measurements);
        Assert.Contains("swiftaid.ellai.voice.turn.recovery", measurements);
        Assert.Equal(0, nonEmptyTagSets);
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
    public void CompletedTurnEventsKeepSessionSequenceAndHistoryAcrossProviderReconnects()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        EllaiCompletedVoiceTurn firstTurn;
        using (var firstStream = coordinator.BeginStream("session-reconnect", CancellationToken.None))
        {
            CompleteTurn(coordinator, firstStream, "muse-1", "Show my assigned cases.");
            firstTurn = Assert.Single(coordinator.GetCompletedTurns("session-reconnect"));
        }

        using var secondStream = coordinator.BeginStream("session-reconnect", CancellationToken.None);
        Assert.True(secondStream.Epoch > firstTurn.StreamEpoch);
        Assert.Contains(firstTurn, coordinator.GetCompletedTurns("session-reconnect"));

        CompleteTurn(coordinator, secondStream, "muse-1", "Show urgent cases only.");
        var turns = coordinator.GetCompletedTurns("session-reconnect");

        Assert.Equal(2, turns.Count);
        Assert.Equal(firstTurn.Sequence + 1, turns[1].Sequence);
        Assert.NotEqual(firstTurn.TurnId, turns[1].TurnId);
        Assert.Equal(secondStream.Epoch, turns[1].StreamEpoch);
        Assert.Equal([turns[1]], coordinator.GetCompletedTurns("session-reconnect", firstTurn.Sequence));
    }

    [Fact]
    public void ReplayedAudioSpanAfterReconnectDoesNotPublishTheSameTurnAgain()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        EllaiCompletedVoiceTurn firstTurn;
        using (var firstStream = coordinator.BeginStream(
                   "session-replay", CancellationToken.None, "capture-replay-1"))
        {
            CompleteTurnWithAudioSpan(coordinator, firstStream, "muse-first", "Show my cases.", 1_000);
            firstTurn = Assert.Single(coordinator.GetCompletedTurns("session-replay"));
        }

        using var reconnectedStream = coordinator.BeginStream(
            "session-replay", CancellationToken.None, "capture-replay-1");
        CompleteTurnWithAudioSpan(
            coordinator, reconnectedStream, "muse-replayed", "Show my cases.", 1_000);

        Assert.Single(coordinator.GetCompletedTurns("session-replay"));
        Assert.Equal(1_000, firstTurn.AudioStartSample);
        Assert.Equal(4_072, firstTurn.AudioEndSample);

        CompleteTurnWithAudioSpan(
            coordinator, reconnectedStream, "muse-next", "Show urgent cases.", 5_000, firstSequence: 26);

        var turns = coordinator.GetCompletedTurns("session-replay");
        Assert.Equal(2, turns.Count);
        Assert.Equal("Show urgent cases.", turns[1].Transcript);
        Assert.Equal(5_000, turns[1].AudioStartSample);
        Assert.Equal(firstTurn.Sequence + 1, turns[1].Sequence);

        using var newCapture = coordinator.BeginStream(
            "session-replay", CancellationToken.None, "capture-replay-2");
        CompleteTurnWithAudioSpan(
            coordinator, newCapture, "muse-new-recording", "Show my cases.", 1_000);
        turns = coordinator.GetCompletedTurns("session-replay");
        Assert.Equal(3, turns.Count);
        Assert.Equal("capture-replay-2", turns[2].VoiceCaptureId);
    }

    [Fact]
    public void FailedTurnCanEnterRecoveryAndTheNextStreamReturnsToListening()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        using var failed = coordinator.BeginStream("session-recovery", CancellationToken.None);

        Assert.True(failed.MarkRecovering());
        Assert.Equal(EllaiVoiceTurnState.Recovering, coordinator.GetSnapshot("session-recovery").State);

        using var retry = coordinator.BeginStream("session-recovery", CancellationToken.None);

        Assert.Equal(EllaiVoiceTurnState.Listening, coordinator.GetSnapshot("session-recovery").State);
    }

    [Fact]
    public void PartialTranscriptIsDisplayOnlyBoundedAndFencedByTurnAndEpoch()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        using var first = coordinator.BeginStream("session-partial", CancellationToken.None);

        Assert.True(first.ObservePartialTranscript("turn-1", "  transient words  "));
        Assert.Equal("transient words", coordinator.GetSnapshot("session-partial").PartialTranscript);
        Assert.False(first.ObservePartialTranscript("turn-2", new string('x', 8_001)));

        using var current = coordinator.BeginStream("session-partial", CancellationToken.None);

        Assert.Null(coordinator.GetSnapshot("session-partial").PartialTranscript);
        Assert.False(first.ObservePartialTranscript("turn-1", "late partial"));
        Assert.True(current.ObservePartialTranscript("turn-2", "current partial"));
        Assert.NotNull(current.AcceptFinalTranscript("turn-2", "current final"));
        Assert.Null(coordinator.GetSnapshot("session-partial").PartialTranscript);
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
        Assert.Equal(EllaiVoiceTurnState.Listening, coordinator.GetSnapshot("session-1").State);
        Assert.Equal(EllaiVoiceTurnState.Listening, coordinator.GetSnapshot("session-2").State);
        Assert.Empty(coordinator.GetCompletedTurns("session-1"));
        Assert.Empty(coordinator.GetCompletedTurns("session-2"));
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

    [Fact]
    public void ClientBargeInMustMatchTheActiveStreamAndPlaybackAndBindsTheNextCompletedTurn()
    {
        var coordinator = new EllaiVoiceTurnCoordinator();
        const string sessionId = "session-client-barge-in";
        const long responseSequence = 12;
        var playbackId = Guid.NewGuid();
        var interruptionTurnId = Guid.NewGuid();
        coordinator.SetAssistantPlayback(sessionId, responseSequence, playbackId, isPlaying: true);
        using var stream = coordinator.BeginStream(sessionId, CancellationToken.None);

        Assert.False(coordinator.ConfirmClientBargeIn(
            sessionId, stream.Epoch + 1, responseSequence, playbackId, interruptionTurnId));
        Assert.False(coordinator.ConfirmClientBargeIn(
            sessionId, stream.Epoch, responseSequence + 1, playbackId, interruptionTurnId));
        Assert.True(coordinator.ConfirmClientBargeIn(
            sessionId, stream.Epoch, responseSequence, playbackId, interruptionTurnId));
        Assert.Equal(EllaiVoiceTurnState.AssistantSpeaking, coordinator.GetSnapshot(sessionId).State);

        for (var sequence = 0; sequence < 6; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Speech(sequence), VadFrameDuration);
        }
        Assert.Equal(EllaiVoiceTurnState.UserSpeaking, coordinator.GetSnapshot(sessionId).State);

        CompleteTurn(coordinator, stream, "muse-turn-1", "Actually, show urgent cases only.", 6);

        var completed = Assert.Single(coordinator.GetCompletedTurns(sessionId));
        Assert.Equal(interruptionTurnId, completed.TurnId);
        Assert.Equal(stream.Epoch, completed.StreamEpoch);
    }

    private static VoiceActivityResult Speech(long sequence) => new(sequence, 0.9f, true, false);
    private static VoiceActivityResult Silence(long sequence) => new(sequence, 0.05f, false, false);

    private static void CompleteTurn(
        EllaiVoiceTurnCoordinator coordinator,
        EllaiVoiceTurnCoordinator.VoiceStreamLease stream,
        string providerTurnId,
        string transcript,
        long firstSequence = 0)
    {
        for (var sequence = firstSequence; sequence < firstSequence + 4; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Speech(sequence), VadFrameDuration);
        }
        for (var sequence = firstSequence + 4; sequence < firstSequence + 26; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Silence(sequence), VadFrameDuration);
        }

        Assert.NotNull(stream.AcceptFinalTranscript(providerTurnId, transcript));
    }

    private static void CompleteTurnWithAudioSpan(
        EllaiVoiceTurnCoordinator coordinator,
        EllaiVoiceTurnCoordinator.VoiceStreamLease stream,
        string providerTurnId,
        string transcript,
        long startSample,
        long firstSequence = 0)
    {
        for (var sequence = firstSequence; sequence < firstSequence + 4; sequence++)
        {
            var start = startSample + ((sequence - firstSequence) * 768);
            coordinator.ObserveAcousticFrame(stream,
                new VoiceActivityResult(sequence, 0.9f, true, false, start, start + 768), VadFrameDuration);
        }
        for (var sequence = firstSequence + 4; sequence < firstSequence + 26; sequence++)
        {
            coordinator.ObserveAcousticFrame(stream, Silence(sequence), VadFrameDuration);
        }

        Assert.NotNull(stream.AcceptFinalTranscript(providerTurnId, transcript));
    }
}
