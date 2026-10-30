namespace SwiftAid.Application.Ellai;

/// <summary>One session-scoped UI event for the Ellai event transport.
/// Carries only safe display routing (review identity, plan binding, state),
/// never record content, model text or approval tokens.</summary>
public sealed record EllaiSessionEvent(
    long Sequence,
    string SessionId,
    string Kind,
    string? Ref,
    string Detail,
    DateTimeOffset AtUtc);

/// <summary>Bounded poll result with the cursor the client passes back.</summary>
public sealed record EllaiSessionEventPage(
    IReadOnlyList<EllaiSessionEvent> Events,
    long Cursor);

/// <summary>Session-scoped UI event journal backing the ApprovalRequired and
/// session-lifecycle transport (AP-01 trigger path). In-memory and bounded;
/// a restarted process loses undelivered UI hints, never business effects —
/// clients always re-fetch the canonical review before accepting a choice.
/// Event gaps recover by cursor replay; replay never replays commands.</summary>
public sealed class EllaiSessionEventJournal
{
    public const int MaxEventsPerSession = 500;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<EllaiSessionEvent>> _bySession = new(StringComparer.Ordinal);
    private long _sequence;

    public EllaiSessionEvent Append(string sessionId, string kind, string? @ref, string detail, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var events))
            {
                events = new LinkedList<EllaiSessionEvent>();
                _bySession[sessionId] = events;
            }

            var @event = new EllaiSessionEvent(++_sequence, sessionId, kind, @ref, detail, now);
            events.AddLast(@event);
            while (events.Count > MaxEventsPerSession)
            {
                events.RemoveFirst();
            }

            return @event;
        }
    }

    /// <summary>Events after the cursor with the new cursor. Unknown sessions
    /// read as empty: the journal never reveals whether a session exists.</summary>
    public EllaiSessionEventPage ReadSince(string sessionId, long cursor, int take = 50)
    {
        take = Math.Clamp(take, 1, 100);
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var events))
            {
                return new EllaiSessionEventPage(Array.Empty<EllaiSessionEvent>(), cursor);
            }

            var page = events.Where(e => e.Sequence > cursor).Take(take).ToList();
            var next = page.Count == 0 ? cursor : page[^1].Sequence;
            return new EllaiSessionEventPage(page, next);
        }
    }
}
