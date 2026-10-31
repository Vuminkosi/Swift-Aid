using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Durable Ellai session record with fencing versions.</summary>
public sealed record EllaiSessionRecord(
    string SessionId,
    EllaiMode Mode,
    string Status,
    long SessionVersion,
    long ControlFence,
    long InteractionEpoch,
    long SpeechEpoch,
    int CyclesUsed,
    int CyclesAllowed,
    string? OwnerActorId,
    string ScopeEpoch,
    string? ActiveInvestigationId,
    string? PendingCheckpointId,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Accepted-turn checkpoint linkage.</summary>
public sealed record EllaiCheckpoint(
    string CheckpointId,
    string SessionId,
    long SessionVersion,
    string? TurnId,
    IReadOnlyList<string> TopicRefs,
    IReadOnlyList<string> PendingReceiptIds,
    DateTimeOffset CreatedAtUtc);

public interface IEllaiSessionStore
{
    Task<EllaiSessionRecord?> GetAsync(string sessionId, CancellationToken ct);

    Task<EllaiSessionRecord> CreateAsync(EllaiSessionRecord record, CancellationToken ct);

    /// <summary>Compare-and-set by expected version; returns null on fence conflict.</summary>
    Task<EllaiSessionRecord?> TryUpdateAsync(EllaiSessionRecord record, long expectedVersion, CancellationToken ct);

    /// <summary>Atomically fences the session update and idempotency identity.
    /// An existing identity returns its current accepted record even when the
    /// caller retries with the pre-acceptance version.</summary>
    Task<EllaiUtteranceStoreResult?> TryAcceptUtteranceAsync(
        EllaiSessionRecord nextRecord,
        long expectedVersion,
        string clientUtteranceId,
        CancellationToken ct,
        string? outboxPayloadJson = null);

    Task SaveCheckpointAsync(EllaiCheckpoint checkpoint, CancellationToken ct);
}

/// <summary>Utterance acceptance: the current view plus whether this call
/// replayed an already-accepted utterance identity. Replays return the
/// current view without consuming a cycle or re-triggering reasoning.</summary>
public sealed record EllaiUtteranceAcceptance(EllaiSessionView View, bool Replay);

public sealed record EllaiUtteranceStoreResult(EllaiSessionRecord Record, bool Replay);

/// <summary>Lease-protected work item recovered from the durable accepted-turn
/// outbox. Payloads are server-created and contain only approved/sanitized input.</summary>
public sealed record EllaiUtteranceOutboxItem(
    string SessionId,
    string ClientUtteranceId,
    string PayloadJson,
    int AttemptCount,
    DateTimeOffset? AcceptedAtUtc = null);

public interface IEllaiUtteranceOutboxStore
{
    Task<IReadOnlyList<EllaiConversationTurn>> ReadConversationAsync(
        string sessionId, string ownerActorId, CancellationToken ct);

    Task<EllaiUtteranceOutboxItem?> ClaimNextAsync(
        DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct);

    Task<EllaiUtteranceResolution?> CompleteAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset completedAtUtc, CancellationToken ct, EllaiUtteranceResolution? resolution = null);

    Task FailAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset retryAtUtc, bool terminal, CancellationToken ct);
}

/// <summary>Thread-safe in-memory store for tests and local development.
/// Production persistence (PostgreSQL, ELL-T04) must implement the same
/// interface with transactional outbox writes.</summary>
public sealed class InMemoryEllaiSessionStore : IEllaiSessionStore, IEllaiUtteranceOutboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiSessionRecord> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SessionId, string ClientUtteranceId), InMemoryOutboxEntry> _utterances = new();
    private readonly List<EllaiCheckpoint> _checkpoints = new();

    public Task<EllaiSessionRecord?> GetAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            _sessions.TryGetValue(sessionId, out var record);
            return Task.FromResult(record);
        }
    }

    public Task<EllaiSessionRecord> CreateAsync(EllaiSessionRecord record, CancellationToken ct)
    {
        lock (_gate)
        {
            _sessions[record.SessionId] = record;
            return Task.FromResult(record);
        }
    }

    public Task<EllaiSessionRecord?> TryUpdateAsync(EllaiSessionRecord record, long expectedVersion, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(record.SessionId, out var current)
                || current.SessionVersion != expectedVersion)
            {
                return Task.FromResult<EllaiSessionRecord?>(null);
            }

            _sessions[record.SessionId] = record;
            return Task.FromResult<EllaiSessionRecord?>(record);
        }
    }

    public Task<EllaiUtteranceStoreResult?> TryAcceptUtteranceAsync(
        EllaiSessionRecord nextRecord,
        long expectedVersion,
        string clientUtteranceId,
        CancellationToken ct,
        string? outboxPayloadJson = null)
    {
        ArgumentNullException.ThrowIfNull(nextRecord);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientUtteranceId);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(nextRecord.SessionId, out var current)
                || current.Status is "Cancelled" or "Completed")
            {
                return Task.FromResult<EllaiUtteranceStoreResult?>(null);
            }

            if (_utterances.TryGetValue((nextRecord.SessionId, clientUtteranceId), out var existing))
            {
                return Task.FromResult<EllaiUtteranceStoreResult?>(
                    new EllaiUtteranceStoreResult(current, true));
            }

            if (current.SessionVersion != expectedVersion
                || current.CyclesUsed >= current.CyclesAllowed
                || !string.Equals(current.OwnerActorId, nextRecord.OwnerActorId, StringComparison.Ordinal))
            {
                return Task.FromResult<EllaiUtteranceStoreResult?>(null);
            }

            _utterances.Add((nextRecord.SessionId, clientUtteranceId),
                new InMemoryOutboxEntry(outboxPayloadJson, null, 0, null) { Sequence = nextRecord.SessionVersion, UserText = ReadUserText(outboxPayloadJson) });
            _sessions[nextRecord.SessionId] = nextRecord;
            return Task.FromResult<EllaiUtteranceStoreResult?>(
                new EllaiUtteranceStoreResult(nextRecord, false));
        }
    }

    public Task<EllaiUtteranceOutboxItem?> ClaimNextAsync(
        DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var entry in _utterances.Where(pair => pair.Value.PayloadJson is not null
                && pair.Value.CompletedAtUtc is null
                && pair.Value.AttemptCount >= 3
                && (pair.Value.LeaseExpiresAtUtc is null || pair.Value.LeaseExpiresAtUtc <= nowUtc)).ToArray())
            {
                _utterances[entry.Key] = entry.Value with
                {
                    PayloadJson = null,
                    LeaseExpiresAtUtc = null,
                    CompletedAtUtc = nowUtc,
                    Outcome = "TerminalFailure",
                };
            }

            var next = _utterances
                .Where(pair => pair.Value.PayloadJson is not null
                    && pair.Value.CompletedAtUtc is null
                    && pair.Value.AttemptCount < 3
                    && (pair.Value.LeaseExpiresAtUtc is null || pair.Value.LeaseExpiresAtUtc <= nowUtc)
                    && !_utterances.Any(other => other.Key.SessionId == pair.Key.SessionId
                        && other.Value.PayloadJson is not null
                        && other.Value.CompletedAtUtc is null
                        && (other.Value.LeaseExpiresAtUtc is null || other.Value.LeaseExpiresAtUtc > nowUtc)
                        && (other.Value.AcceptedAtUtc < pair.Value.AcceptedAtUtc
                            || other.Value.AcceptedAtUtc == pair.Value.AcceptedAtUtc
                                && string.CompareOrdinal(other.Key.ClientUtteranceId, pair.Key.ClientUtteranceId) < 0))
                    && !_utterances.Any(other => other.Key.SessionId == pair.Key.SessionId
                        && !ReferenceEquals(other.Value, pair.Value)
                        && other.Value.PayloadJson is not null
                        && other.Value.CompletedAtUtc is null
                        && other.Value.LeaseExpiresAtUtc > nowUtc))
                .OrderBy(pair => pair.Value.AcceptedAtUtc)
                .FirstOrDefault();
            if (next.Value is null)
            {
                return Task.FromResult<EllaiUtteranceOutboxItem?>(null);
            }

            var attempts = next.Value.AttemptCount + 1;
            _utterances[next.Key] = next.Value with
            {
                AttemptCount = attempts,
                Outcome = "Processing",
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
            };
            return Task.FromResult<EllaiUtteranceOutboxItem?>(new(
                next.Key.SessionId, next.Key.ClientUtteranceId, next.Value.PayloadJson!, attempts, next.Value.AcceptedAtUtc));
        }
    }

    public Task<IReadOnlyList<EllaiConversationTurn>> ReadConversationAsync(
        string sessionId, string ownerActorId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.OwnerActorId != ownerActorId)
                return Task.FromResult<IReadOnlyList<EllaiConversationTurn>>(Array.Empty<EllaiConversationTurn>());
            return Task.FromResult<IReadOnlyList<EllaiConversationTurn>>(_utterances
                .Where(pair => pair.Key.SessionId == sessionId)
                .OrderByDescending(pair => pair.Value.Sequence).Take(100).Reverse()
                .Select(pair => new EllaiConversationTurn(sessionId, pair.Key.ClientUtteranceId,
                    pair.Value.Sequence, pair.Value.UserText, pair.Value.AssistantText,
                    pair.Value.Outcome, pair.Value.AttemptCount, pair.Value.CompletedAtUtc)).ToArray());
        }
    }

    private static string? ReadUserText(string? payload)
    {
        if (payload is null) return null;
        try { return EllaiUtteranceWorkEnvelope.Deserialize(payload).Utterance; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    public Task<EllaiUtteranceResolution?> CompleteAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset completedAtUtc, CancellationToken ct, EllaiUtteranceResolution? resolution = null)
    {
        resolution ??= EllaiUtteranceResolution.Failed;
        resolution.Validate();
        lock (_gate)
        {
            var key = (sessionId, clientUtteranceId);
            if (_utterances.TryGetValue(key, out var item)
                && item.CompletedAtUtc is null && item.AttemptCount == attemptCount)
            {
                if (resolution.ExpectedInteractionEpoch.HasValue && (!_sessions.TryGetValue(sessionId, out var session)
                    || session.Status != "Active" || session.InteractionEpoch != resolution.ExpectedInteractionEpoch))
                    resolution = EllaiUtteranceResolution.Cancelled;
                _utterances[key] = item with
                {
                    PayloadJson = null, LeaseExpiresAtUtc = null, CompletedAtUtc = completedAtUtc,
                    Outcome = resolution.Outcome, AssistantText = resolution.AssistantText,
                };
                return Task.FromResult<EllaiUtteranceResolution?>(resolution);
            }
        }
        return Task.FromResult<EllaiUtteranceResolution?>(null);
    }

    public Task FailAsync(
        string sessionId, string clientUtteranceId, int attemptCount,
        DateTimeOffset retryAtUtc, bool terminal, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = (sessionId, clientUtteranceId);
            if (_utterances.TryGetValue(key, out var item)
                && item.CompletedAtUtc is null
                && item.AttemptCount == attemptCount)
            {
                _utterances[key] = terminal || item.AttemptCount >= 3
                    ? item with { PayloadJson = null, LeaseExpiresAtUtc = null, CompletedAtUtc = retryAtUtc, Outcome = "TerminalFailure" }
                    : item with { LeaseExpiresAtUtc = retryAtUtc, Outcome = "RetryableFailure" };
            }
        }

        return Task.CompletedTask;
    }

    private sealed record InMemoryOutboxEntry(
        string? PayloadJson,
        DateTimeOffset? LeaseExpiresAtUtc,
        int AttemptCount,
        DateTimeOffset? CompletedAtUtc)
    {
        public DateTimeOffset AcceptedAtUtc { get; } = DateTimeOffset.UtcNow;
        public long Sequence { get; init; }
        public string? UserText { get; init; }
        public string? AssistantText { get; init; }
        public string Outcome { get; init; } = "Queued";
    }

    public Task SaveCheckpointAsync(EllaiCheckpoint checkpoint, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_checkpoints.Any(c => c.CheckpointId == checkpoint.CheckpointId))
            {
                return Task.CompletedTask;
            }

            _checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Session lifecycle: start, versioned utterance acceptance,
/// checkpointing, pause/stop with fencing. Cancelled sessions never resume;
/// restart creates a new session/epoch. (ELL-T04 groundwork.)</summary>
public sealed class EllaiSessionService(IEllaiSessionStore store, EllaiConsentService? consent = null)
{
    /// <summary>Signed-in session cycle ceiling. Anonymous guest sessions do
    /// not have an arbitrary turn-count ceiling.</summary>
    public const int DefaultCyclesAllowed = 12;
    public const int GuestCyclesAllowed = int.MaxValue;

    public async Task<EllaiSessionView?> GetOwnedViewAsync(
        string sessionId, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct)
    {
        Authorization.ActorContext actor;
        try { actor = Authorization.ActorContext.FromValidatedPrincipal(principal); }
        catch (UnauthorizedAccessException) { return null; }
        var record = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        return record is not null
            && !string.IsNullOrWhiteSpace(record.OwnerActorId)
            && string.Equals(record.OwnerActorId, actor.ActorId, StringComparison.Ordinal)
            ? ToView(record)
            : null;
    }

    public async Task<EllaiSessionView> StartAsync(
        StartSessionRequest request,
        System.Security.Claims.ClaimsPrincipal principal,
        SwiftAid.Contracts.Sync.ScopeManifest scope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        var actor = Authorization.ActorContext.FromValidatedPrincipal(principal);
        if (scope.AccessEpoch != actor.AccessEpoch
            || scope.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new UnauthorizedAccessException("A current account scope is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            throw new ArgumentException("ClientRequestId is required.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var scopeDisplay = scope.Grants.Count == 0
            ? $"ellai-help-identity-epoch-{scope.AccessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"access-epoch-{scope.AccessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var cyclesAllowed = string.Equals(
            principal.FindFirst("swiftaid_ellai_guest")?.Value,
            "true",
            StringComparison.Ordinal)
            ? GuestCyclesAllowed
            : DefaultCyclesAllowed;
        var record = new EllaiSessionRecord(
            $"ellai-{Guid.NewGuid():N}", request.Mode, "Active", 1, 1, 1, 1,
            0, cyclesAllowed, actor.ActorId,
            scopeDisplay,
            null, null, now);
        await store.CreateAsync(record, ct);
        return ToView(record);
    }

    /// <summary>Test-only start path for lifecycle tests that do not exercise
    /// trusted scope resolution. Production callers must supply the current
    /// grant snapshot to the overload above.</summary>
    internal async Task<EllaiSessionView> StartAsync(
        StartSessionRequest request,
        System.Security.Claims.ClaimsPrincipal principal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = Authorization.ActorContext.FromValidatedPrincipal(principal);
        if (string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            throw new ArgumentException("ClientRequestId is required.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var record = new EllaiSessionRecord(
            $"ellai-{Guid.NewGuid():N}", request.Mode, "Active", 1, 1, 1, 1,
            0, DefaultCyclesAllowed, actor.ActorId, $"test-scope-{actor.AccessEpoch}",
            null, null, now);
        await store.CreateAsync(record, ct);
        return ToView(record);
    }

    /// <summary>Accepts a finalized utterance; stale expected versions are
    /// rejected so late turns cannot overwrite newer state. Replaying an
    /// already-accepted (session, client-utterance) identity returns the
    /// current view without consuming another cycle (§7.1).</summary>
    internal async Task<EllaiUtteranceAcceptance?> SubmitUtteranceAsync(
        SubmitUtteranceRequest request,
        Authorization.ActorContext actor,
        CancellationToken ct,
        string? outboxPayloadJson = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(request.ClientUtteranceId))
        {
            throw new ArgumentException("ClientUtteranceId is required.", nameof(request));
        }

        var current = await store.GetAsync(request.SessionId, ct);
        if (current is null || current.Status is "Cancelled" or "Completed")
        {
            return null;
        }

        if (!string.Equals(current.OwnerActorId, actor.ActorId, StringComparison.Ordinal))
        {
            return null;
        }

        var next = current with
        {
            SessionVersion = current.SessionVersion + 1,
            InteractionEpoch = current.InteractionEpoch + 1,
            CyclesUsed = current.CyclesUsed + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var accepted = await store.TryAcceptUtteranceAsync(
            next, request.ExpectedSessionVersion, request.ClientUtteranceId, ct, outboxPayloadJson).ConfigureAwait(false);
        return accepted is null
            ? null
            : new EllaiUtteranceAcceptance(ToView(accepted.Record), accepted.Replay);
    }

    public async Task<EllaiCheckpoint?> SaveCheckpointAsync(
        string sessionId, string? turnId, IReadOnlyList<string> topicRefs,
        IReadOnlyList<string> pendingReceiptIds, CancellationToken ct)
    {
        var current = await store.GetAsync(sessionId, ct);
        if (current is null || current.Status is "Cancelled" or "Completed")
        {
            return null;
        }

        var checkpoint = new EllaiCheckpoint(
            $"cp-{Guid.NewGuid():N}", sessionId, current.SessionVersion,
            turnId, topicRefs, pendingReceiptIds, DateTimeOffset.UtcNow);
        await store.SaveCheckpointAsync(checkpoint, ct);

        var next = current with
        {
            SessionVersion = current.SessionVersion + 1,
            PendingCheckpointId = checkpoint.CheckpointId,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var updated = await store.TryUpdateAsync(next, current.SessionVersion, ct);
        return updated is null ? null : checkpoint;
    }

    /// <summary>Cancel terminates the session and revokes unused authority;
    /// committed effects are reconciled separately, never undone here.</summary>
    public async Task<bool> CancelAsync(
        string sessionId,
        long expectedVersion,
        System.Security.Claims.ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var actor = Authorization.ActorContext.FromValidatedPrincipal(principal);
        var current = await store.GetAsync(sessionId, ct);
        if (current is null
            || !string.Equals(current.OwnerActorId, actor.ActorId, StringComparison.Ordinal))
        {
            return false;
        }

        if (current.Status == "Cancelled") return true;
        if (current.Status == "Completed") return false;
        if (current.SessionVersion != expectedVersion) return false;

        var next = current with
        {
            SessionVersion = current.SessionVersion + 1,
            ControlFence = current.ControlFence + 1,
            SpeechEpoch = current.SpeechEpoch + 1,
            Status = "Cancelled",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return await store.TryUpdateAsync(next, expectedVersion, ct) is not null;
    }

    private EllaiSessionView ToView(EllaiSessionRecord record) => new(
        record.SessionId, record.Mode, record.ActiveInvestigationId, record.Status,
        record.ScopeEpoch, consent?.VersionLabel(record.SessionId) ?? "consent/1", "caps/1",
        record.CyclesUsed, record.CyclesAllowed,
        null, 0, record.SessionVersion);
}
