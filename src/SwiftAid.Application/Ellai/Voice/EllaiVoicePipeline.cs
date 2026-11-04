namespace SwiftAid.Application.Ellai;

/// <summary>Transcript event kind from the ASR transport.</summary>
public enum TranscriptEventKind
{
    Partial,
    SpeechEnd,
    Completed,
    Cancelled
}

/// <summary>Raw transport event; SpeechEnd is NOT final text.</summary>
public sealed record TranscriptTransportEvent(
    string ProviderTurnId,
    TranscriptEventKind Kind,
    string Text,
    long StreamEpoch);

/// <summary>Normalized utterance for the Ellai loop.</summary>
public sealed record NormalizedUtterance(
    string UtteranceId,
    string ProviderTurnId,
    string Text,
    bool IsFinal,
    long StreamEpoch);

/// <summary>Mode-specific transcript turn normalizer (ELL-T11 groundwork).
/// Cumulative partials never append duplicate text; speechEnd never executes;
/// interleaved completed turns correlate exactly once per provider turn;
/// late completions for an older stream epoch are dropped.</summary>
public sealed class TranscriptTurnNormalizer
{
    private readonly object _gate = new();
    private readonly HashSet<string> _finalizedProviderTurns = new(StringComparer.Ordinal);
    private long _currentEpoch = 1;

    public void AdvanceEpoch() { lock (_gate) { _currentEpoch++; } }

    public IReadOnlyList<NormalizedUtterance> Normalize(
        IReadOnlyList<TranscriptTransportEvent> events, long activeEpoch)
    {
        ArgumentNullException.ThrowIfNull(events);
        var accepted = new List<NormalizedUtterance>();
        lock (_gate)
        {
            foreach (var e in events)
            {
                if (e.StreamEpoch != activeEpoch || e.StreamEpoch < _currentEpoch - 64)
                {
                    continue;
                }

                if (e.Kind is TranscriptEventKind.Partial or TranscriptEventKind.SpeechEnd)
                {
                    continue;
                }

                if (e.Kind == TranscriptEventKind.Cancelled)
                {
                    continue;
                }

                if (!_finalizedProviderTurns.Add(e.ProviderTurnId))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(e.Text))
                {
                    continue;
                }

                accepted.Add(new NormalizedUtterance(
                    $"utt-{Guid.NewGuid():N}", e.ProviderTurnId, e.Text.Trim(), true, e.StreamEpoch));
            }
        }

        return accepted;
    }
}

/// <summary>Validated narration sentence attached to session/turn/speech epoch.</summary>
public sealed record NarrationSentence(
    string SentenceId,
    string Text,
    string SessionId,
    string? TurnId,
    long SpeechEpoch,
    string EvidenceVersion);

/// <summary>Gateway-side narration coordination queue (ELL-T12 groundwork).
/// This stores validated text only; it does not synthesize or play audio.
/// At most two sentences queue ahead. Interruption bumps the speech epoch and
/// drops stale text before any Gateway synthesis/streaming adapter can consume it.</summary>
public sealed class EllaiNarrationQueue
{
    private readonly object _gate = new();
    private readonly Queue<NarrationSentence> _queue = new();
    private long _speechEpoch = 1;

    public const int MaxQueued = 2;

    public long SpeechEpoch { get { lock (_gate) { return _speechEpoch; } } }

    public bool TryEnqueue(NarrationSentence sentence)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        lock (_gate)
        {
            if (sentence.SpeechEpoch != _speechEpoch)
            {
                return false;
            }

            if (_queue.Count >= MaxQueued)
            {
                return false;
            }

            _queue.Enqueue(sentence);
            return true;
        }
    }

    /// <summary>Barge-in: stop TTS, cancel queued narration, fence the epoch.</summary>
    public long Interrupt()
    {
        lock (_gate)
        {
            _queue.Clear();
            return ++_speechEpoch;
        }
    }

    public NarrationSentence? Dequeue(long activeEpoch)
    {
        lock (_gate)
        {
            if (!_queue.TryPeek(out var next) || next.SpeechEpoch != activeEpoch)
            {
                if (_queue.Count > 0 && next?.SpeechEpoch != activeEpoch)
                {
                    _queue.Clear();
                }

                return null;
            }

            return _queue.Dequeue();
        }
    }
}
