namespace SwiftAid.Application.Ellai;

public enum EllaiVoiceTurnState
{
    Idle,
    Listening,
    SpeechCandidate,
    UserSpeaking,
    EndpointCandidate,
    TurnCompleted,
    Processing,
    AssistantSpeaking,
    Interrupted,
    Recovering,
}

public sealed record EllaiVoiceTurnOptions(
    int SpeechStartConfirmationMs = 120,
    int EndOfTurnSilenceMs = 650,
    int BargeInConfirmationMs = 180,
    int MaximumTurnDurationMs = 480_000)
{
    public void Validate()
    {
        if (SpeechStartConfirmationMs is < 1 or > 2_000
            || EndOfTurnSilenceMs is < 100 or > 5_000
            || BargeInConfirmationMs is < 1 or > 2_000
            || MaximumTurnDurationMs is < 1_000 or > 900_000)
        {
            throw new ArgumentOutOfRangeException(nameof(EllaiVoiceTurnOptions));
        }
    }
}

public sealed record EllaiVoiceTurnSnapshot(
    string SessionId,
    long Epoch,
    EllaiVoiceTurnState State,
    long? ActiveResponseSequence,
    Guid? ActivePlaybackId,
    TimeSpan ConfirmedSpeech,
    TimeSpan Silence,
    string? PartialTranscript);

public sealed record EllaiAcceptedFinalTranscript(
    string ProviderTurnId,
    string Text,
    long Epoch);

public sealed record EllaiCompletedVoiceTurn(
    long Sequence,
    string ProviderTurnId,
    string Transcript,
    Guid TurnId,
    long StreamEpoch,
    long AudioStartSample = -1,
    long AudioEndSample = -1,
    string VoiceCaptureId = "");

public sealed record EllaiBargeInDecision(
    string SessionId,
    long ResponseSequence,
    Guid PlaybackId,
    long Epoch);

/// <summary>Deterministic per-session voice turn state. Acoustic activity is
/// advisory; only provider-final transcripts are returned for submission.</summary>
public sealed class EllaiVoiceTurnCoordinator
{
    private const int MaximumProviderTurnsPerStream = 512;
    private const int MaximumRetainedCompletedTurnsPerSession = 512;
    private const int MaximumRetainedSessions = 4_096;
    private static readonly TimeSpan SessionRetention = TimeSpan.FromMinutes(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly EllaiVoiceTurnOptions _options;
    private readonly TimeProvider _timeProvider;
    private long _nextEpoch;

    public EllaiVoiceTurnCoordinator(EllaiVoiceTurnOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new EllaiVoiceTurnOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public VoiceStreamLease BeginStream(
        string sessionId, CancellationToken cancellationToken, string? voiceCaptureId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            PruneSessions();
            if (!_sessions.TryGetValue(sessionId, out var state))
            {
                state = new SessionState(sessionId, _timeProvider.GetUtcNow());
                _sessions.Add(sessionId, state);
            }

            state.ActiveStream?.Cancel();
            state.Epoch = Interlocked.Increment(ref _nextEpoch);
            var effectiveCaptureId = string.IsNullOrWhiteSpace(voiceCaptureId) || voiceCaptureId.Length > 64
                ? $"epoch:{state.Epoch}"
                : voiceCaptureId;
            state.LastTouchedUtc = _timeProvider.GetUtcNow();
            state.StreamStartedTimestamp = _timeProvider.GetTimestamp();
            state.FinalProviderTurnIds.Clear();
            state.PendingFinalTranscripts.Clear();
            state.PartialProviderTurnId = null;
            state.PartialTranscript = null;
            state.LastVadSequence = -1;
            state.SpeechCandidate = TimeSpan.Zero;
            state.SpeechCandidateStartSample = -1;
            state.LastSpeechStartSample = -1;
            state.LastSpeechEndSample = -1;
            state.BargeInStartSample = -1;
            state.ConfirmedSpeech = TimeSpan.Zero;
            state.Silence = TimeSpan.Zero;
            state.BargeInSpeech = TimeSpan.Zero;
            state.LastInterruptedResponseSequence = null;
            state.LastInterruptedPlaybackId = null;
            state.LastInterruptedEpoch = 0;
            state.PendingInterruptionTurnId = null;
            state.ConfirmedInterruptionTurnId = null;
            state.State = state.ActivePlaybackId.HasValue
                ? EllaiVoiceTurnState.AssistantSpeaking
                : EllaiVoiceTurnState.Listening;
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var lease = new VoiceStreamLease(
                this, state, state.Epoch, effectiveCaptureId, linkedCancellation);
            state.ActiveStream = lease;
            return lease;
        }
    }

    public EllaiVoiceTurnSnapshot? SetAssistantPlayback(
        string sessionId, long sequence, Guid playbackId, bool isPlaying)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sequence < 1 || playbackId == Guid.Empty)
        {
            return null;
        }

        lock (_gate)
        {
            var state = GetOrCreateSession(sessionId);
            state.LastTouchedUtc = _timeProvider.GetUtcNow();
            if (isPlaying)
            {
                state.ActiveResponseSequence = sequence;
                state.ActivePlaybackId = playbackId;
                state.BargeInSpeech = TimeSpan.Zero;
                state.State = EllaiVoiceTurnState.AssistantSpeaking;
            }
            else if (state.ActiveResponseSequence == sequence && state.ActivePlaybackId == playbackId)
            {
                state.ActiveResponseSequence = null;
                state.ActivePlaybackId = null;
                state.BargeInSpeech = TimeSpan.Zero;
                if (state.State == EllaiVoiceTurnState.AssistantSpeaking)
                {
                    state.State = EllaiVoiceTurnState.Listening;
                }
            }

            return Snapshot(state);
        }
    }

    public bool InterruptPlayback(string sessionId, long sequence, Guid playbackId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var state)
                || state.ActiveResponseSequence != sequence
                || state.ActivePlaybackId != playbackId)
            {
                return false;
            }

            MarkInterrupted(state);
            return true;
        }
    }

    /// <summary>Accepts a client VAD confirmation only for the current
    /// microphone epoch and the exact response playback identity.</summary>
    public bool ConfirmClientBargeIn(
        string sessionId,
        long streamEpoch,
        long responseSequence,
        Guid playbackId,
        Guid turnId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch < 1 || responseSequence < 1
            || playbackId == Guid.Empty || turnId == Guid.Empty)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var state)
                || state.ActiveStream is not { } activeStream
                || activeStream.Epoch != streamEpoch
                || state.Epoch != streamEpoch)
            {
                return false;
            }

            var isCurrentPlayback = state.ActiveResponseSequence == responseSequence
                && state.ActivePlaybackId == playbackId;
            var gatewayAlreadyInterrupted = state.LastInterruptedEpoch == streamEpoch
                && state.LastInterruptedResponseSequence == responseSequence
                && state.LastInterruptedPlaybackId == playbackId;
            if (!isCurrentPlayback && !gatewayAlreadyInterrupted)
            {
                return false;
            }

            if (isCurrentPlayback)
            {
                state.PendingInterruptionTurnId ??= turnId;
            }
            else
            {
                state.ConfirmedInterruptionTurnId ??= turnId;
            }
            state.LastTouchedUtc = _timeProvider.GetUtcNow();
            return true;
        }
    }

    public EllaiBargeInDecision? ObserveAcousticFrame(
        VoiceStreamLease lease,
        VoiceActivityResult activity,
        TimeSpan frameDuration)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(activity);
        if (frameDuration <= TimeSpan.Zero || frameDuration > TimeSpan.FromSeconds(1)
            || !float.IsFinite(activity.SpeechProbability)
            || activity.SpeechProbability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameDuration));
        }

        lock (_gate)
        {
            var state = lease.Session;
            if (!IsCurrent(lease) || lease.CancellationToken.IsCancellationRequested
                || activity.Sequence <= state.LastVadSequence)
            {
                return null;
            }

            if (state.LastVadSequence >= 0 && activity.Sequence != state.LastVadSequence + 1
                || activity.StateWasReset)
            {
                ResetAcousticState(state);
            }
            state.LastVadSequence = activity.Sequence;
            state.LastTouchedUtc = _timeProvider.GetUtcNow();

            var isSpeech = activity.IsSpeech && activity.SpeechProbability >= 0.5f;
            if (state.ActivePlaybackId.HasValue)
            {
                if (isSpeech)
                {
                    if (state.BargeInSpeech == TimeSpan.Zero)
                    {
                        state.BargeInStartSample = activity.AudioStartSample;
                    }
                    state.BargeInSpeech += frameDuration;
                    if (state.BargeInSpeech.TotalMilliseconds >= _options.BargeInConfirmationMs)
                    {
                        var decision = new EllaiBargeInDecision(
                            state.SessionId,
                            state.ActiveResponseSequence!.Value,
                            state.ActivePlaybackId.Value,
                            state.Epoch);
                        EllaiVoiceTurnMetrics.BargeInConfirmed(state.BargeInSpeech);
                        MarkInterrupted(state);
                        state.ConfirmedInterruptionTurnId = state.PendingInterruptionTurnId;
                        state.PendingInterruptionTurnId = null;
                        state.State = EllaiVoiceTurnState.UserSpeaking;
                        state.ConfirmedSpeech += state.BargeInSpeech;
                        state.LastSpeechStartSample = state.BargeInStartSample;
                        state.LastSpeechEndSample = activity.AudioEndSample;
                        state.BargeInSpeech = TimeSpan.Zero;
                        return decision;
                    }
                }
                else
                {
                    state.BargeInSpeech = TimeSpan.Zero;
                }
            }

            AdvanceAcousticState(state, isSpeech, frameDuration, activity);
            return null;
        }
    }

    public EllaiAcceptedFinalTranscript? AcceptFinalTranscript(
        VoiceStreamLease lease, string providerTurnId, string text)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (string.IsNullOrWhiteSpace(providerTurnId) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Trim();
        if (normalized.Length > 8_000)
        {
            return null;
        }

        lock (_gate)
        {
            var state = lease.Session;
            if (!IsCurrent(lease) || lease.CancellationToken.IsCancellationRequested
                || state.FinalProviderTurnIds.Count >= MaximumProviderTurnsPerStream
                || !state.FinalProviderTurnIds.Add(providerTurnId))
            {
                return null;
            }

            state.LastTouchedUtc = _timeProvider.GetUtcNow();
            if (string.Equals(state.PartialProviderTurnId, providerTurnId, StringComparison.Ordinal)
                || string.Equals(state.PartialProviderTurnId, "unscoped", StringComparison.Ordinal))
            {
                state.PartialProviderTurnId = null;
                state.PartialTranscript = null;
            }
            EllaiVoiceTurnMetrics.FinalTranscriptAccepted(
                _timeProvider.GetElapsedTime(state.StreamStartedTimestamp));
            var accepted = new EllaiAcceptedFinalTranscript(providerTurnId, normalized, lease.Epoch);
            state.PendingFinalTranscripts.Enqueue(accepted);
            CompletePendingTranscripts(state);
            return accepted;
        }
    }

    public bool ObservePartialTranscript(
        VoiceStreamLease lease, string providerTurnId, string text)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (string.IsNullOrWhiteSpace(providerTurnId) || providerTurnId.Length > 128
            || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        if (normalized.Length > 8_000)
        {
            return false;
        }

        lock (_gate)
        {
            var state = lease.Session;
            if (!IsCurrent(lease) || lease.CancellationToken.IsCancellationRequested
                || state.FinalProviderTurnIds.Contains(providerTurnId))
            {
                return false;
            }

            state.PartialProviderTurnId = providerTurnId;
            state.PartialTranscript = normalized;
            state.LastTouchedUtc = _timeProvider.GetUtcNow();
            return true;
        }
    }

    public bool MarkRecovering(VoiceStreamLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsCurrent(lease) || lease.CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            lease.Session.State = EllaiVoiceTurnState.Recovering;
            lease.Session.LastTouchedUtc = _timeProvider.GetUtcNow();
            lease.Session.PartialProviderTurnId = null;
            lease.Session.PartialTranscript = null;
            EllaiVoiceTurnMetrics.TurnRecovered();
            return true;
        }
    }

    public EllaiVoiceTurnSnapshot GetSnapshot(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var state)
                ? Snapshot(state)
                : new EllaiVoiceTurnSnapshot(sessionId, 0, EllaiVoiceTurnState.Idle,
                    null, null, TimeSpan.Zero, TimeSpan.Zero, null);
        }
    }

    public IReadOnlyList<EllaiCompletedVoiceTurn> GetCompletedTurns(
        string sessionId,
        long afterSequence = 0)
    {
        if (afterSequence < 0)
        {
            return [];
        }

        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var state)
                ? state.CompletedTurns.Where(turn => turn.Sequence > afterSequence).ToArray()
                : [];
        }
    }

    private SessionState GetOrCreateSession(string sessionId)
    {
        PruneSessions();
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            return state;
        }

        state = new SessionState(sessionId, _timeProvider.GetUtcNow());
        _sessions.Add(sessionId, state);
        return state;
    }

    private void PruneSessions()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var key in _sessions.Where(pair => pair.Value.ActiveStream is null
                     && !pair.Value.ActivePlaybackId.HasValue
                     && pair.Value.LastTouchedUtc + SessionRetention <= now)
                 .Select(pair => pair.Key).ToArray())
        {
            _sessions.Remove(key);
        }

        if (_sessions.Count >= MaximumRetainedSessions)
        {
            foreach (var key in _sessions.Where(pair => pair.Value.ActiveStream is null
                         && !pair.Value.ActivePlaybackId.HasValue)
                     .OrderBy(pair => pair.Value.LastTouchedUtc)
                     .Take(_sessions.Count - MaximumRetainedSessions + 1)
                     .Select(pair => pair.Key).ToArray())
            {
                _sessions.Remove(key);
            }
        }
    }

    private static EllaiVoiceTurnSnapshot Snapshot(SessionState state) =>
        new(state.SessionId, state.Epoch, state.State,
            state.ActiveResponseSequence, state.ActivePlaybackId,
            state.ConfirmedSpeech, state.Silence, state.PartialTranscript);

    private static void MarkInterrupted(SessionState state)
    {
        if (state.ActiveResponseSequence is long responseSequence
            && state.ActivePlaybackId is Guid playbackId)
        {
            state.LastInterruptedResponseSequence = responseSequence;
            state.LastInterruptedPlaybackId = playbackId;
            state.LastInterruptedEpoch = state.Epoch;
        }
        state.ActiveResponseSequence = null;
        state.ActivePlaybackId = null;
        state.BargeInSpeech = TimeSpan.Zero;
        state.SpeechCandidate = TimeSpan.Zero;
        state.Silence = TimeSpan.Zero;
        state.State = EllaiVoiceTurnState.Interrupted;
    }

    private static void ResetAcousticState(SessionState state)
    {
        state.SpeechCandidate = TimeSpan.Zero;
        state.SpeechCandidateStartSample = -1;
        state.ConfirmedSpeech = TimeSpan.Zero;
        state.Silence = TimeSpan.Zero;
        state.LastSpeechStartSample = -1;
        state.LastSpeechEndSample = -1;
        if (!state.ActivePlaybackId.HasValue)
        {
            state.State = EllaiVoiceTurnState.Listening;
        }
    }

    private void AdvanceAcousticState(
        SessionState state, bool isSpeech, TimeSpan frameDuration, VoiceActivityResult activity)
    {
        switch (state.State)
        {
            case EllaiVoiceTurnState.Listening:
                if (isSpeech)
                {
                    state.State = EllaiVoiceTurnState.SpeechCandidate;
                    state.SpeechCandidate = frameDuration;
                    state.SpeechCandidateStartSample = activity.AudioStartSample;
                }
                break;
            case EllaiVoiceTurnState.SpeechCandidate:
                if (!isSpeech)
                {
                    state.SpeechCandidate = TimeSpan.Zero;
                    state.State = EllaiVoiceTurnState.Listening;
                    break;
                }

                state.SpeechCandidate += frameDuration;
                if (state.SpeechCandidate.TotalMilliseconds >= _options.SpeechStartConfirmationMs)
                {
                    state.ConfirmedSpeech = state.SpeechCandidate;
                    state.LastSpeechStartSample = state.SpeechCandidateStartSample;
                    state.LastSpeechEndSample = activity.AudioEndSample;
                    state.SpeechCandidate = TimeSpan.Zero;
                    state.SpeechCandidateStartSample = -1;
                    state.State = EllaiVoiceTurnState.UserSpeaking;
                    EllaiVoiceTurnMetrics.SpeechStartConfirmed(state.ConfirmedSpeech);
                }
                break;
            case EllaiVoiceTurnState.UserSpeaking:
                if (isSpeech)
                {
                    state.ConfirmedSpeech += frameDuration;
                    state.Silence = TimeSpan.Zero;
                    state.LastSpeechEndSample = activity.AudioEndSample;
                }
                else
                {
                    state.Silence += frameDuration;
                    if (state.Silence.TotalMilliseconds >= _options.EndOfTurnSilenceMs)
                    {
                        state.State = EllaiVoiceTurnState.EndpointCandidate;
                        EllaiVoiceTurnMetrics.EndpointCandidateReached(state.Silence);
                        CompletePendingTranscripts(state);
                    }
                }
                break;
            case EllaiVoiceTurnState.EndpointCandidate:
                if (isSpeech)
                {
                    state.State = EllaiVoiceTurnState.UserSpeaking;
                    state.ConfirmedSpeech += frameDuration;
                    state.Silence = TimeSpan.Zero;
                    state.LastSpeechEndSample = activity.AudioEndSample;
                }
                else
                {
                    state.Silence += frameDuration;
                }
                break;
            case EllaiVoiceTurnState.TurnCompleted:
                if (isSpeech)
                {
                    state.State = EllaiVoiceTurnState.UserSpeaking;
                    state.ConfirmedSpeech = frameDuration;
                    state.Silence = TimeSpan.Zero;
                    state.LastSpeechStartSample = activity.AudioStartSample;
                    state.LastSpeechEndSample = activity.AudioEndSample;
                }
                break;
            case EllaiVoiceTurnState.Interrupted:
                if (isSpeech)
                {
                    state.State = EllaiVoiceTurnState.UserSpeaking;
                    state.ConfirmedSpeech += frameDuration;
                    if (state.LastSpeechStartSample < 0)
                    {
                        state.LastSpeechStartSample = activity.AudioStartSample;
                    }
                    state.LastSpeechEndSample = activity.AudioEndSample;
                }
                break;
        }
    }

    private static void CompletePendingTranscripts(SessionState state)
    {
        if (state.State != EllaiVoiceTurnState.EndpointCandidate)
        {
            return;
        }

        while (state.PendingFinalTranscripts.TryDequeue(out var transcript))
        {
            if (state.CompletedTurns.Count >= MaximumProviderTurnsPerStream)
            {
                state.PendingFinalTranscripts.Clear();
                break;
            }

            var hasAudioSpan = state.LastSpeechStartSample >= 0
                && state.LastSpeechEndSample > state.LastSpeechStartSample;
            var audioSpan = (state.ActiveStream?.VoiceCaptureId ?? string.Empty,
                state.LastSpeechStartSample, state.LastSpeechEndSample);
            if (hasAudioSpan && state.CompletedAudioSpans.TryGetValue(audioSpan, out var priorEpoch)
                && priorEpoch < transcript.Epoch)
            {
                state.State = EllaiVoiceTurnState.TurnCompleted;
                continue;
            }
            if (hasAudioSpan)
            {
                var isNewSpan = !state.CompletedAudioSpans.ContainsKey(audioSpan);
                state.CompletedAudioSpans[audioSpan] = transcript.Epoch;
                if (isNewSpan)
                {
                    state.CompletedAudioSpanOrder.Enqueue(audioSpan);
                }
                while (state.CompletedAudioSpanOrder.Count > MaximumRetainedCompletedTurnsPerSession)
                {
                    var expiredSpan = state.CompletedAudioSpanOrder.Dequeue();
                    if (state.CompletedAudioSpans.TryGetValue(expiredSpan, out var expiredEpoch)
                        && expiredEpoch <= transcript.Epoch)
                    {
                        state.CompletedAudioSpans.Remove(expiredSpan);
                    }
                }
            }

            while (state.CompletedTurns.Count >= MaximumRetainedCompletedTurnsPerSession)
            {
                state.CompletedTurns.Dequeue();
            }

            var turnId = state.ConfirmedInterruptionTurnId ?? Guid.NewGuid();
            state.ConfirmedInterruptionTurnId = null;
            state.CompletedTurns.Enqueue(new EllaiCompletedVoiceTurn(
                ++state.NextCompletionSequence,
                transcript.ProviderTurnId,
                transcript.Text,
                turnId,
                transcript.Epoch,
                hasAudioSpan ? state.LastSpeechStartSample : -1,
                hasAudioSpan ? state.LastSpeechEndSample : -1,
                state.ActiveStream?.VoiceCaptureId ?? string.Empty));
            state.PendingInterruptionTurnId = null;
            state.ConfirmedInterruptionTurnId = null;
            state.State = EllaiVoiceTurnState.TurnCompleted;
        }
    }

    private bool IsCurrent(VoiceStreamLease lease) =>
        _sessions.TryGetValue(lease.Session.SessionId, out var current)
        && ReferenceEquals(current, lease.Session)
        && ReferenceEquals(current.ActiveStream, lease)
        && current.Epoch == lease.Epoch;

    private void End(VoiceStreamLease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(lease.Session.ActiveStream, lease))
            {
                lease.Session.ActiveStream = null;
                lease.Session.PartialProviderTurnId = null;
                lease.Session.PartialTranscript = null;
            }

            lease.DisposeCancellation();
        }
    }

    internal sealed class SessionState(string sessionId, DateTimeOffset createdAtUtc)
    {
        public string SessionId { get; } = sessionId;
        public long Epoch { get; set; }
        public VoiceStreamLease? ActiveStream { get; set; }
        public HashSet<string> FinalProviderTurnIds { get; } = new(StringComparer.Ordinal);
        public Queue<EllaiAcceptedFinalTranscript> PendingFinalTranscripts { get; } = new();
        public Queue<EllaiCompletedVoiceTurn> CompletedTurns { get; } = new();
        public Dictionary<(string CaptureId, long Start, long End), long> CompletedAudioSpans { get; } = [];
        public Queue<(string CaptureId, long Start, long End)> CompletedAudioSpanOrder { get; } = new();
        public long NextCompletionSequence { get; set; }
        public long LastVadSequence { get; set; } = -1;
        public long? ActiveResponseSequence { get; set; }
        public Guid? ActivePlaybackId { get; set; }
        public long? LastInterruptedResponseSequence { get; set; }
        public Guid? LastInterruptedPlaybackId { get; set; }
        public long LastInterruptedEpoch { get; set; }
        public Guid? PendingInterruptionTurnId { get; set; }
        public Guid? ConfirmedInterruptionTurnId { get; set; }
        public EllaiVoiceTurnState State { get; set; } = EllaiVoiceTurnState.Idle;
        public TimeSpan SpeechCandidate { get; set; }
        public long SpeechCandidateStartSample { get; set; } = -1;
        public long LastSpeechStartSample { get; set; } = -1;
        public long LastSpeechEndSample { get; set; } = -1;
        public long BargeInStartSample { get; set; } = -1;
        public TimeSpan ConfirmedSpeech { get; set; }
        public TimeSpan Silence { get; set; }
        public TimeSpan BargeInSpeech { get; set; }
        public DateTimeOffset LastTouchedUtc { get; set; } = createdAtUtc;
        public long StreamStartedTimestamp { get; set; }
        public string? PartialProviderTurnId { get; set; }
        public string? PartialTranscript { get; set; }
    }

    public sealed class VoiceStreamLease : IDisposable
    {
        private readonly EllaiVoiceTurnCoordinator _owner;
        private readonly CancellationTokenSource _cancellation;
        private int _disposed;

        internal VoiceStreamLease(
            EllaiVoiceTurnCoordinator owner,
            SessionState session,
            long epoch,
            string voiceCaptureId,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            Session = session;
            Epoch = epoch;
            VoiceCaptureId = voiceCaptureId;
            _cancellation = cancellation;
        }

        internal SessionState Session { get; }
        public long Epoch { get; }
        public string VoiceCaptureId { get; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public string SessionId => Session.SessionId;

        public EllaiAcceptedFinalTranscript? AcceptFinalTranscript(string providerTurnId, string text) =>
            _owner.AcceptFinalTranscript(this, providerTurnId, text);

        public bool ObservePartialTranscript(string providerTurnId, string text) =>
            _owner.ObservePartialTranscript(this, providerTurnId, text);

        public bool MarkRecovering() => _owner.MarkRecovering(this);

        internal void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        internal void DisposeCancellation() => _cancellation.Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.End(this);
            }
        }
    }
}
