using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Short-lived, session-bound current-turn display output. Outputs
/// are intentionally in memory only: this store is not a conversation-history
/// or evidence store and is cleared on session cancellation.</summary>
public sealed class EllaiSessionResponseStore(TimeProvider? timeProvider = null)
{
    public const int MaximumTextLength = 2000;
    public const int MaximumResponsesPerSession = 16;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, SessionResponses> _sessions = new(StringComparer.Ordinal);

    public EllaiSessionResponseDto? Publish(string sessionId, string clientUtteranceId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientUtteranceId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var boundedText = text.Trim();
        if (boundedText.Length is 0 or > MaximumTextLength)
        {
            return null;
        }

        lock (_gate)
        {
            PruneExpired();
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new SessionResponses(_timeProvider.GetUtcNow());
                _sessions.Add(sessionId, session);
            }
            else if (string.Equals(session.LastClientUtteranceId, clientUtteranceId, StringComparison.Ordinal))
            {
                return session.Items.Last?.Value;
            }

            var response = new EllaiSessionResponseDto(
                ++session.Sequence, sessionId, boundedText, _timeProvider.GetUtcNow());
            session.LastUpdatedAtUtc = response.CreatedAtUtc;
            session.LastClientUtteranceId = clientUtteranceId;
            session.Items.AddLast(response);
            while (session.Items.Count > MaximumResponsesPerSession)
            {
                session.Items.RemoveFirst();
            }

            return response;
        }
    }

    public EllaiSessionResponsePageDto ReadSince(string sessionId, long cursor, int take = 16)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        take = Math.Clamp(take, 1, MaximumResponsesPerSession);
        lock (_gate)
        {
            PruneExpired();
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return new EllaiSessionResponsePageDto(Array.Empty<EllaiSessionResponseDto>(), Math.Max(0, cursor));
            }

            var responses = session.Items.Where(item => item.Sequence > Math.Max(0, cursor))
                .Take(take).ToArray();
            return new EllaiSessionResponsePageDto(
                responses,
                responses.Length == 0 ? Math.Max(0, cursor) : responses[^1].Sequence);
        }
    }

    public void Clear(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }
    }

    private void PruneExpired()
    {
        var expiredAt = _timeProvider.GetUtcNow() - Lifetime;
        foreach (var key in _sessions.Where(pair => pair.Value.LastUpdatedAtUtc <= expiredAt)
                     .Select(pair => pair.Key).ToArray())
        {
            _sessions.Remove(key);
        }
    }

    private sealed class SessionResponses(DateTimeOffset createdAtUtc)
    {
        public long Sequence { get; set; }
        public string? LastClientUtteranceId { get; set; }
        public DateTimeOffset LastUpdatedAtUtc { get; set; } = createdAtUtc;
        public LinkedList<EllaiSessionResponseDto> Items { get; } = new();
    }
}
