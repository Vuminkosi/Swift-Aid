using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Short-lived, session-bound current-turn display output. Outputs
/// are intentionally in memory only: this store is not a conversation-history
/// or evidence store and is cleared on session cancellation.</summary>
public sealed class EllaiSessionResponseStore(TimeProvider? timeProvider = null, IEllaiResponseAudioPreparation? audioPreparation = null)
{
    public const int MaximumTextLength = 2000;
    public const int MaximumResponsesPerSession = 64;
    public string StreamId { get; } = Guid.NewGuid().ToString("N");

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, SessionResponses> _sessions = new(StringComparer.Ordinal);

    /// <summary>Capture the current ephemeral feed generation before starting work.</summary>
    public Guid CaptureGeneration(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            PruneExpired();
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new SessionResponses(_timeProvider.GetUtcNow());
                _sessions.Add(sessionId, session);
            }
            return session.Generation;
        }
    }

    /// <summary>Publish a committed result only while its captured feed remains current.</summary>
    public bool PublishCommitted(string sessionId, Guid generation, string utteranceId,
        EllaiUtteranceResolution resolution, IReadOnlyList<string> chunks, string language = "eng", string? englishVoiceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(utteranceId);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(chunks);
        resolution.Validate();
        if (chunks.Count > 0 && (resolution.Outcome != "Completed" || chunks.Any(string.IsNullOrWhiteSpace) ||
            !string.Equals(string.Join(" ", chunks.Select(chunk => chunk.Trim())),
                resolution.AssistantText?.Trim(), StringComparison.Ordinal)))
            throw new ArgumentException("Response chunks must match the committed answer.", nameof(chunks));
        lock (_gate)
        {
            PruneExpired();
            if (!_sessions.TryGetValue(sessionId, out var session) || session.Generation != generation)
                return false;
            for (var index = 0; index < chunks.Count; index++)
                PublishChunk(sessionId, utteranceId, index + 1, chunks[index], englishVoiceId);
            Reconcile(sessionId, utteranceId, resolution, language, englishVoiceId);
            return true;
        }
    }

    public EllaiSessionResponseDto? PublishProgress(
        string sessionId, string utteranceId, string text, EllaiInteractionProgressDto progress, string? englishVoiceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(utteranceId);
        lock (_gate)
        {
            PruneExpired();
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new SessionResponses(_timeProvider.GetUtcNow());
                _sessions.Add(sessionId, session);
            }
            if (session.Items.Any(item => item.Progress?.EventId == progress.EventId)) return null;
            var response = new EllaiSessionResponseDto(++session.Sequence, sessionId, text,
                _timeProvider.GetUtcNow(), utteranceId, IsFinal: false, Progress: progress, EnglishVoiceId: englishVoiceId);
            session.Items.AddLast(response);
            session.LastUpdatedAtUtc = response.CreatedAtUtc;
            while (session.Items.Count > MaximumResponsesPerSession) session.Items.RemoveFirst();
            audioPreparation?.Published(response);
            return response;
        }
    }

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
            else if (session.Items.LastOrDefault(item => item.IsFinal
                && string.Equals(item.ClientUtteranceId, clientUtteranceId, StringComparison.Ordinal)) is { } existing)
            {
                return existing;
            }

            var response = new EllaiSessionResponseDto(
                ++session.Sequence, sessionId, boundedText, _timeProvider.GetUtcNow(), clientUtteranceId);
            session.LastUpdatedAtUtc = response.CreatedAtUtc;
            session.LastClientUtteranceId = clientUtteranceId;
            session.Items.AddLast(response);
            while (session.Items.Count > MaximumResponsesPerSession)
            {
                session.Items.RemoveFirst();
            }

            audioPreparation?.Published(response);
            return response;
        }
    }

    public bool CanSpeakProgress(EllaiSessionResponseDto response)
    {
        lock (_gate)
        {
            if (response.Progress is { State: "Guidance", Speak: true } narration)
                return narration.ExpiresAtUtc > _timeProvider.GetUtcNow()
                    && _sessions.TryGetValue(response.SessionId, out var guidanceSession)
                    && !guidanceSession.Items.Any(item => item.Sequence > response.Sequence);
            return response.Progress is { Speak: true } progress
                && progress.ExpiresAtUtc > _timeProvider.GetUtcNow()
                && _sessions.TryGetValue(response.SessionId, out var session)
                && !session.Items.Any(item => item.ClientUtteranceId == response.ClientUtteranceId
                    && item.Progress?.State == "Interrupted")
                && !session.Items.Any(item => item.Sequence > response.Sequence
                    && (item.ClientUtteranceId != response.ClientUtteranceId
                        || item.Progress is null
                        || item.Progress.State is "Completed" or "Failed" or "Cancelled" or "Interrupted" or "Retrying"));
        }
    }

    public void InterruptProgress(string sessionId, long responseSequence)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;
            var response = session.Items.FirstOrDefault(item => item.Sequence == responseSequence);
            if (response?.ClientUtteranceId is not { } turnId) return;
            PublishProgress(sessionId, turnId, string.Empty,
                new($"{turnId}:interrupted", "Interrupted", "Response interrupted", false,
                    response.Progress?.ControllerEpoch ?? 0, _timeProvider.GetUtcNow().AddSeconds(30)));
        }
    }

    public EllaiSessionResponseDto? PublishChunk(
        string sessionId, string clientUtteranceId, int chunkIndex, string text, string? englishVoiceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientUtteranceId);
        if (chunkIndex < 1 || string.IsNullOrWhiteSpace(text))
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

            var deliveryId = $"{clientUtteranceId}:chunk:{chunkIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (session.Chunks.TryGetValue(deliveryId, out var existing))
            {
                if (session.Items.First is { } first && existing.Sequence >= first.Value.Sequence)
                    return existing;
                session.Chunks.Remove(deliveryId);
            }

            var response = new EllaiSessionResponseDto(
                ++session.Sequence, sessionId, boundedText, _timeProvider.GetUtcNow(), clientUtteranceId, IsFinal: false,
                EnglishVoiceId: englishVoiceId);
            session.LastUpdatedAtUtc = response.CreatedAtUtc;
            session.LastClientUtteranceId = deliveryId;
            session.Items.AddLast(response);
            session.Chunks[deliveryId] = response;
            while (session.Items.Count > MaximumResponsesPerSession)
            {
                session.Items.RemoveFirst();
            }
            var oldest = session.Items.First!.Value.Sequence;
            foreach (var key in session.Chunks.Where(item => item.Value.Sequence < oldest).Select(item => item.Key).ToArray())
                session.Chunks.Remove(key);
            foreach (var key in session.ProvisionalFences.Keys.Where(sequence => sequence < oldest).ToArray())
                session.ProvisionalFences.Remove(key);
            audioPreparation?.Published(response);
            return response;
        }
    }

    /// <summary>Publish a validated segment only for the captured feed generation and first active attempt.</summary>
    public bool PublishProvisional(string sessionId, Guid generation, string utteranceId, int attempt,
        string scopeEpoch, long interactionEpoch, int chunkIndex, string text, string? englishVoiceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(utteranceId);
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        lock (_gate)
        {
            PruneExpired();
            if (!_sessions.TryGetValue(sessionId, out var session) || session.Generation != generation)
                return false;
            if (session.StreamAttempts.TryGetValue(utteranceId, out var previous) && previous != attempt)
                return false;
            session.StreamAttempts[utteranceId] = attempt;
            var response = PublishChunk(sessionId, utteranceId, chunkIndex, text, englishVoiceId);
            if (response is null) return false;
            session.ProvisionalFences[response.Sequence] = (attempt, scopeEpoch, interactionEpoch);
            return true;
        }
    }

    /// <summary>Checks a provisional chunk against the current durable attempt and session epoch.</summary>
    public bool TryGetProvisionalState(string sessionId, long sequence, out bool provisional)
    {
        lock (_gate)
        {
            provisional = false;
            if (!_sessions.TryGetValue(sessionId, out var session) || !session.Items.Any(item => item.Sequence == sequence))
                return false;
            provisional = session.ProvisionalFences.ContainsKey(sequence);
            return true;
        }
    }

    public bool CanDeliver(string sessionId, EllaiSessionResponseDto response, string scopeEpoch, long interactionEpoch,
        IReadOnlyList<EllaiConversationTurn> conversation, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return false;
            if (!session.Items.Any(item => item.Sequence == response.Sequence)) return false;
            if (!session.ProvisionalFences.TryGetValue(response.Sequence, out var fence)) return true;
            if (fence.Epoch != interactionEpoch || fence.ScopeEpoch != scopeEpoch) return false;
            var turn = conversation.FirstOrDefault(item => item.ClientUtteranceId == response.ClientUtteranceId);
            return turn is not null && turn.AttemptCount == fence.Attempt &&
                (turn.Outcome == "Completed" && turn.CompletedAtUtc is not null ||
                 turn.Outcome == "Processing" && turn.CompletedAtUtc is null && turn.LeaseExpiresAtUtc > now);
        }
    }

    public bool ContainsUtterance(string sessionId, string clientUtteranceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientUtteranceId);
        lock (_gate)
        {
            PruneExpired();
            return _sessions.TryGetValue(sessionId, out var session)
                && session.Items.Any(item => item.Progress is null && string.Equals(
                    item.ClientUtteranceId, clientUtteranceId, StringComparison.Ordinal));
        }
    }

    public EllaiSessionResponseDto Reconcile(string sessionId, string utteranceId, EllaiUtteranceResolution resolution,
        string language = "eng", string? englishVoiceId = null)
    {
        resolution.Validate();
        lock (_gate)
        {
            PruneExpired();
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new SessionResponses(_timeProvider.GetUtcNow());
                _sessions.Add(sessionId, session);
            }
            var existing = session.Items.LastOrDefault(item => item.IsReconciliation
                && item.ClientUtteranceId == utteranceId);
            if (existing is not null) return existing;
            var response = new EllaiSessionResponseDto(++session.Sequence, sessionId,
                resolution.AssistantText ?? string.Empty, _timeProvider.GetUtcNow(), utteranceId,
                IsFinal: true, IsReconciliation: true, Outcome: resolution.Outcome, Language: language,
                EnglishVoiceId: englishVoiceId);
            session.Items.AddLast(response);
            session.LastUpdatedAtUtc = response.CreatedAtUtc;
            while (session.Items.Count > MaximumResponsesPerSession) session.Items.RemoveFirst();
            audioPreparation?.Published(response);
            return response;
        }
    }

    public EllaiSessionResponseDto? CompleteUtterance(string sessionId, string clientUtteranceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientUtteranceId);
        lock (_gate)
        {
            PruneExpired();
            if (!_sessions.TryGetValue(sessionId, out var session)
                || !session.Items.Any(item => string.Equals(
                    item.ClientUtteranceId, clientUtteranceId, StringComparison.Ordinal)))
            {
                return null;
            }

            if (session.Items.LastOrDefault(item => item.IsFinal && item.Text.Length == 0
                && string.Equals(item.ClientUtteranceId, clientUtteranceId, StringComparison.Ordinal)) is { } existing)
            {
                return existing;
            }

            var response = new EllaiSessionResponseDto(
                ++session.Sequence, sessionId, string.Empty, _timeProvider.GetUtcNow(), clientUtteranceId, IsFinal: true);
            session.LastUpdatedAtUtc = response.CreatedAtUtc;
            session.LastClientUtteranceId = $"{clientUtteranceId}:complete";
            session.Items.AddLast(response);
            while (session.Items.Count > MaximumResponsesPerSession)
            {
                session.Items.RemoveFirst();
            }
            audioPreparation?.Published(response);
            return response;
        }
    }

    public EllaiSessionResponsePageDto ReadSince(string sessionId, long cursor, int take = 16, bool includeProgress = true)
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
                .Where(item => includeProgress || item.Progress is null)
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
            audioPreparation?.Clear(sessionId);
        }
    }

    private void PruneExpired()
    {
        var expiredAt = _timeProvider.GetUtcNow() - Lifetime;
        foreach (var key in _sessions.Where(pair => pair.Value.LastUpdatedAtUtc <= expiredAt)
                     .Select(pair => pair.Key).ToArray())
        {
            _sessions.Remove(key);
            audioPreparation?.Clear(key);
        }
    }

    private sealed class SessionResponses(DateTimeOffset createdAtUtc)
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public long Sequence { get; set; }
        public string? LastClientUtteranceId { get; set; }
        public DateTimeOffset LastUpdatedAtUtc { get; set; } = createdAtUtc;
        public LinkedList<EllaiSessionResponseDto> Items { get; } = new();
        public Dictionary<string, EllaiSessionResponseDto> Chunks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> StreamAttempts { get; } = new(StringComparer.Ordinal);
        public Dictionary<long, (int Attempt, string ScopeEpoch, long Epoch)> ProvisionalFences { get; } = new();
    }
}
