namespace SwiftAid.Application.Ellai;

public enum EllaiVoiceTurnState
{
    Idle,
    Listening,
    SpeechCandidate,
    UserSpeaking,
    EndpointCandidate,
    Processing,
    AssistantSpeaking,
    Interrupted,
    Recovering,
}

public sealed record EllaiVoiceTurnOptions(
    int SpeechStartConfirmationMs = 120,
    int EndOfTurnSilenceMs = 650,
    int BargeInConfirmationMs = 180)
{
    public void Validate()
    {
        if (SpeechStartConfirmationMs is < 1 or > 2_000
            || EndOfTurnSilenceMs is < 100 or > 5_000
            || BargeInConfirmationMs is < 1 or > 2_000)
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
    TimeSpan Silence);

public sealed record EllaiAcceptedFinalTranscript(
    string ProviderTurnId,
    string Text,
    long Epoch);

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

    public VoiceStreamLease BeginStream(string sessionId, CancellationToken cancellationToken)
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
            state.LastTouchedUtc = _timeProvider.GetUtcNow();
            state.FinalProviderTurnIds.Clear();
            state.LastVadSequence = -1;
            state.SpeechCandidate = TimeSpan.Zero;
            state.ConfirmedSpeech = TimeSpan.Zero;
            state.Silence = TimeSpan.Zero;
            state.BargeInSpeech = TimeSpan.Zero;
            state.State = state.ActivePlaybackId.HasValue
                ? EllaiVoiceTurnState.AssistantSpeaking
                : EllaiVoiceTurnState.Listening;
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var lease = new VoiceStreamLease(this, state, state.Epoch, linkedCancellation);
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
                    state.BargeInSpeech += frameDuration;
                    if (state.BargeInSpeech.TotalMilliseconds >= _options.BargeInConfirmationMs)
                    {
                        var decision = new EllaiBargeInDecision(
                            state.SessionId,
                            state.ActiveResponseSequence!.Value,
                            state.ActivePlaybackId.Value,
                            state.Epoch);
                        MarkInterrupted(state);
                        state.State = EllaiVoiceTurnState.UserSpeaking;
                        state.ConfirmedSpeech += state.BargeInSpeech;
                        state.BargeInSpeech = TimeSpan.Zero;
                        return decision;
                    }
                }
                else
                {
                    state.BargeInSpeech = TimeSpan.Zero;
                }
            }

            AdvanceAcousticState(state, isSpeech, frameDuration);
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

            state.State = EllaiVoiceTurnState.Processing;
            state.LastTouchedUtc = _timeProvider.GetUtcNow();
            return new EllaiAcceptedFinalTranscript(providerTurnId, normalized, lease.Epoch);
        }
    }

    public EllaiVoiceTurnSnapshot GetSnapshot(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var state)
                ? Snapshot(state)
                : new EllaiVoiceTurnSnapshot(sessionId, 0, EllaiVoiceTurnState.Idle,
                    null, null, TimeSpan.Zero, TimeSpan.Zero);
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
            state.ConfirmedSpeech, state.Silence);

    private static void MarkInterrupted(SessionState state)
    {
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
        state.ConfirmedSpeech = TimeSpan.Zero;
        state.Silence = TimeSpan.Zero;
        if (!state.ActivePlaybackId.HasValue)
        {
            state.State = EllaiVoiceTurnState.Listening;
        }
    }

    private void AdvanceAcousticState(SessionState state, bool isSpeech, TimeSpan frameDuration)
    {
        switch (state.State)
        {
            case EllaiVoiceTurnState.Listening:
                if (isSpeech)
                {
                    state.State = EllaiVoiceTurnState.SpeechCandidate;
                    state.SpeechCandidate = frameDuration;
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
                    state.SpeechCandidate = TimeSpan.Zero;
                    state.State = EllaiVoiceTurnState.UserSpeaking;
                }
                break;
            case EllaiVoiceTurnState.UserSpeaking:
                if (isSpeech)
                {
                    state.ConfirmedSpeech += frameDuration;
                    state.Silence = TimeSpan.Zero;
                }
                else
                {
                    state.Silence += frameDuration;
                    if (state.Silence.TotalMilliseconds >= _options.EndOfTurnSilenceMs)
                    {
                        state.State = EllaiVoiceTurnState.EndpointCandidate;
                    }
                }
                break;
            case EllaiVoiceTurnState.EndpointCandidate:
                if (isSpeech)
                {
                    state.State = EllaiVoiceTurnState.UserSpeaking;
                    state.ConfirmedSpeech += frameDuration;
                    state.Silence = TimeSpan.Zero;
                }
                else
                {
                    state.Silence += frameDuration;
                }
                break;
            case EllaiVoiceTurnState.Interrupted:
                if (isSpeech)
                {
                    state.State = EllaiVoiceTurnState.UserSpeaking;
                    state.ConfirmedSpeech += frameDuration;
                }
                break;
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
        public long LastVadSequence { get; set; } = -1;
        public long? ActiveResponseSequence { get; set; }
        public Guid? ActivePlaybackId { get; set; }
        public EllaiVoiceTurnState State { get; set; } = EllaiVoiceTurnState.Idle;
        public TimeSpan SpeechCandidate { get; set; }
        public TimeSpan ConfirmedSpeech { get; set; }
        public TimeSpan Silence { get; set; }
        public TimeSpan BargeInSpeech { get; set; }
        public DateTimeOffset LastTouchedUtc { get; set; } = createdAtUtc;
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
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            Session = session;
            Epoch = epoch;
            _cancellation = cancellation;
        }

        internal SessionState Session { get; }
        public long Epoch { get; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public string SessionId => Session.SessionId;

        public EllaiAcceptedFinalTranscript? AcceptFinalTranscript(string providerTurnId, string text) =>
            _owner.AcceptFinalTranscript(this, providerTurnId, text);

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
