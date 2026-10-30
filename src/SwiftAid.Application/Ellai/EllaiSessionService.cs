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

    Task SaveCheckpointAsync(EllaiCheckpoint checkpoint, CancellationToken ct);
}

/// <summary>Thread-safe in-memory store for tests and local development.
/// Production persistence (PostgreSQL, ELL-T04) must implement the same
/// interface with transactional outbox writes.</summary>
public sealed class InMemoryEllaiSessionStore : IEllaiSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiSessionRecord> _sessions = new(StringComparer.Ordinal);
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
public sealed class EllaiSessionService(IEllaiSessionStore store)
{
    public const int DefaultCyclesAllowed = 12;

    public async Task<EllaiSessionView> StartAsync(StartSessionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            throw new ArgumentException("ClientRequestId is required.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var record = new EllaiSessionRecord(
            $"ellai-{Guid.NewGuid():N}", request.Mode, "Active", 1, 1, 1, 1,
            0, DefaultCyclesAllowed, $"scope-{now:yyyyMMddHHmmss}",
            null, null, now);
        await store.CreateAsync(record, ct);
        return ToView(record);
    }

    /// <summary>Accepts a finalized utterance; stale expected versions are
    /// rejected so late/replayed turns cannot overwrite newer state.</summary>
    public async Task<EllaiSessionView?> SubmitUtteranceAsync(SubmitUtteranceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await store.GetAsync(request.SessionId, ct);
        if (current is null || current.Status is "Cancelled" or "Completed")
        {
            return null;
        }

        if (current.SessionVersion != request.ExpectedSessionVersion)
        {
            return null;
        }

        if (current.CyclesUsed >= current.CyclesAllowed)
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
        var updated = await store.TryUpdateAsync(next, request.ExpectedSessionVersion, ct);
        return updated is null ? null : ToView(updated);
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
    public async Task<bool> CancelAsync(string sessionId, long expectedVersion, CancellationToken ct)
    {
        var current = await store.GetAsync(sessionId, ct);
        if (current is null || current.Status == "Cancelled")
        {
            return false;
        }

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

    private static EllaiSessionView ToView(EllaiSessionRecord record) => new(
        record.SessionId, record.Mode, record.ActiveInvestigationId, record.Status,
        record.ScopeEpoch, "consent/1", "caps/1", record.CyclesUsed, record.CyclesAllowed,
        null, 0, record.SessionVersion);
}
