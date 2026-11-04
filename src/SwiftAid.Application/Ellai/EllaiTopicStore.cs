using System.Security.Cryptography;
using System.Text;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>One durable topic entry: the accepted conversational topic with
/// its entity link, source version and checkpoint linkage.</summary>
public sealed record EllaiTopicEntry(
    string TopicId,
    string SessionId,
    string Title,
    IReadOnlyList<string> Aliases,
    string? EntityRef,
    string? EntityVersion,
    string CoverageState,
    string? CheckpointId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Durable topic-history authority. PostgreSQL is the production
/// implementation; the in-memory store keeps unit-test semantics identical.
/// Topic ids are stable hashes of session and title: re-tracking the same
/// title updates one row instead of forking history.</summary>
public interface IEllaiTopicStore
{
    Task<EllaiTopicEntry> RecordAsync(
        string sessionId, string title, IReadOnlyList<string> aliases,
        string? entityRef, string? entityVersion, string? checkpointId,
        DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<EllaiTopicEntry>> ListAsync(string sessionId, CancellationToken cancellationToken);

    async Task<EllaiTopicEntry?> GetAsync(string sessionId, string topicId, CancellationToken ct) =>
        (await ListAsync(sessionId, ct).ConfigureAwait(false)).SingleOrDefault(topic => topic.TopicId == topicId);

    /// <summary>Atomically replace an existing topic payload only when its prior payload and version still match.</summary>
    Task<bool> CompareExchangePayloadAsync(string sessionId, string topicId, string? expectedRef,
        string? expectedVersion, string newRef, string newVersion, DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException("Atomic topic payload updates are unavailable.");

    Task<bool> TryCreateAsync(string sessionId, string title, IReadOnlyList<string> aliases,
        string entityRef, string entityVersion, DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException("Atomic topic creation is unavailable.");
}

/// <summary>Thread-safe in-memory topic store with the same semantics as the
/// durable port.</summary>
public sealed class InMemoryEllaiTopicStore : IEllaiTopicStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiTopicEntry> _byId = new(StringComparer.Ordinal);

    public Task<EllaiTopicEntry> RecordAsync(
        string sessionId, string title, IReadOnlyList<string> aliases,
        string? entityRef, string? entityVersion, string? checkpointId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        lock (_gate)
        {
            var id = StableTopicId(sessionId, title);
            if (_byId.TryGetValue(id, out var existing))
            {
                var updated = existing with
                {
                    Aliases = aliases,
                    EntityRef = entityRef,
                    EntityVersion = entityVersion,
                    CheckpointId = checkpointId ?? existing.CheckpointId,
                    UpdatedAtUtc = now,
                };
                _byId[id] = updated;
                return Task.FromResult(updated);
            }

            var entry = new EllaiTopicEntry(id, sessionId, title, aliases, entityRef,
                entityVersion, "Partial", checkpointId, now, now);
            _byId[id] = entry;
            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<EllaiTopicEntry>> ListAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EllaiTopicEntry>>(
                _byId.Values
                    .Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                    .OrderBy(e => e.CreatedAtUtc)
                    .ToList());
        }
    }

    public Task<bool> CompareExchangePayloadAsync(string sessionId, string topicId, string? expectedRef,
        string? expectedVersion, string newRef, string newVersion, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicId);
        ArgumentNullException.ThrowIfNull(newRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_byId.TryGetValue(topicId, out var current) || current.SessionId != sessionId ||
                current.EntityRef != expectedRef || current.EntityVersion != expectedVersion)
                return Task.FromResult(false);
            _byId[topicId] = current with { EntityRef = newRef, EntityVersion = newVersion, UpdatedAtUtc = now };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryCreateAsync(string sessionId, string title, IReadOnlyList<string> aliases,
        string entityRef, string entityVersion, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(entityRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityVersion);
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var id = StableTopicId(sessionId, title);
            if (_byId.ContainsKey(id)) return Task.FromResult(false);
            _byId[id] = new(id, sessionId, title, aliases, entityRef, entityVersion, "Partial", null, now, now);
            return Task.FromResult(true);
        }
    }

    internal static string StableTopicId(string sessionId, string title)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}|{title}"));
        return "topic-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
