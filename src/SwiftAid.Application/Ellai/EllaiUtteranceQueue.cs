using System.Threading.Channels;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>One accepted utterance bound for background reasoning. Carries
/// only routing stamps and the user's own words; the actor is the
/// server-validated principal captured at submit time, never request data.</summary>
public sealed record EllaiUtteranceWork(
    string SessionId,
    string ScopeEpoch,
    string Utterance,
    string FeatureId,
    string? ResourceId,
    EllaiMode Mode,
    long ControllerEpoch,
    string CycleId,
    ActorContext Actor,
    bool ContributorEligible,
    string ConsentVersion,
    string ProcessingLabel = EllaiProcessingClassification.UnknownOrigin,
    string DataClassification = EllaiProcessingClassification.UnknownOrigin,
    IReadOnlySet<string>? AllowedTools = null,
    string ConsentPurpose = EllaiToolBroker.ToolReadPurpose,
    string ClientUtteranceId = "",
    EllaiLanguagePreferences? Languages = null,
    string? ActivePage = null);

/// <summary>Single-flight utterance handoff: at most one queued-or-running
/// work item per session, latest utterance wins. A newer utterance submitted
/// while its session runs replaces the pending one; it is never dropped and
/// never runs concurrently with its session. Cross-instance fencing still
/// needs a shared lock (Redis); this gate is per process.</summary>
public sealed class EllaiUtteranceQueue(int capacity = 100)
{
    private readonly object _gate = new();
    private readonly Channel<string> _ready =
        Channel.CreateBounded<string>(Math.Clamp(capacity, 1, 10_000));
    private readonly Dictionary<string, EllaiUtteranceWork> _latest = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    /// <summary>Enqueues, replacing any pending item for the session. Never
    /// blocks; false only when the channel is full for a new session.</summary>
    public bool TryEnqueue(EllaiUtteranceWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_active.Contains(work.SessionId) || _latest.ContainsKey(work.SessionId))
            {
                _latest[work.SessionId] = work;
                return true;
            }

            _latest[work.SessionId] = work;
            if (!_ready.Writer.TryWrite(work.SessionId))
            {
                _latest.Remove(work.SessionId);
                return false;
            }

            return true;
        }
    }

    public async ValueTask<EllaiUtteranceWork> DequeueAsync(CancellationToken ct)
    {
        var sessionId = await _ready.Reader.ReadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _active.Add(sessionId);
            return _latest[sessionId];
        }
    }

    /// <summary>Releases the finished item. When a newer utterance arrived
    /// mid-run it is requeued for its own run; otherwise the session gate
    /// opens. Must be called once per dequeued item.</summary>
    public void Complete(string sessionId, EllaiUtteranceWork finished)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(finished);
        lock (_gate)
        {
            if (_latest.TryGetValue(sessionId, out var current) && ReferenceEquals(current, finished))
            {
                _latest.Remove(sessionId);
                _active.Remove(sessionId);
                return;
            }

            if (!_ready.Writer.TryWrite(sessionId))
            {
                _latest.Remove(sessionId);
                _active.Remove(sessionId);
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _latest.Count;
            }
        }
    }
}
