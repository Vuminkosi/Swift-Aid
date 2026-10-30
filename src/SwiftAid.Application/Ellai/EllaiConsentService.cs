namespace SwiftAid.Application.Ellai;

/// <summary>Independent permission dimensions. Opening Ellai or consenting to
/// one never grants the others.</summary>
public enum EllaiConsentDimension
{
    Microphone,
    Transcription,
    ReasoningText,
    CurrentViewSharing,
    DemoControl,
    PersistentMemory,
    HistoryRetention,
    Export,
    BusinessActionApproval
}

/// <summary>Purpose-scoped consent grant with version and expiry.</summary>
public sealed record EllaiConsentGrant(
    string GrantId,
    string SessionId,
    EllaiConsentDimension Dimension,
    string Purpose,
    string DataClass,
    int Version,
    DateTimeOffset ExpiresAtUtc,
    bool Revoked);

/// <summary>Purpose-scoped consent service (ELL-T03). Grants are per-dimension,
/// per-purpose, versioned and expiring; revocation invalidates dependent
/// sessions immediately. A microphone grant never implies reasoning dispatch.</summary>
public sealed class EllaiConsentService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiConsentGrant> _grants = new(StringComparer.Ordinal);
    private int _version;

    public EllaiConsentGrant Grant(
        string sessionId, EllaiConsentDimension dimension, string purpose, string dataClass, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var grant = new EllaiConsentGrant(
            $"grant-{Guid.NewGuid():N}", sessionId, dimension, purpose, dataClass,
            Interlocked.Increment(ref _version), DateTimeOffset.UtcNow.Add(lifetime), false);
        lock (_gate)
        {
            _grants[grant.GrantId] = grant;
        }

        return grant;
    }

    public void Revoke(string grantId)
    {
        lock (_gate)
        {
            if (_grants.TryGetValue(grantId, out var grant))
            {
                _grants[grantId] = grant with { Revoked = true };
            }
        }
    }

    /// <summary>Revokes every grant for the session; returns the count revoked.</summary>
    public int RevokeSession(string sessionId)
    {
        lock (_gate)
        {
            var owned = _grants.Values.Where(g => g.SessionId == sessionId && !g.Revoked).ToList();
            foreach (var grant in owned)
            {
                _grants[grant.GrantId] = grant with { Revoked = true };
            }

            return owned.Count;
        }
    }

    /// <summary>Highest grant version issued for the session, or 0 when none.</summary>
    public int LatestVersion(string sessionId)
    {
        lock (_gate)
        {
            return _grants.Values
                .Where(g => g.SessionId == sessionId)
                .Select(g => g.Version)
                .DefaultIfEmpty(0)
                .Max();
        }
    }

    /// <summary>Consent version label for session projections. Sessions
    /// without any grant report consent/0 — never a fabricated grant.</summary>
    public string VersionLabel(string sessionId) => $"consent/{LatestVersion(sessionId)}";

    /// <summary>Merges durable grants into memory after a restart or from a
    /// sibling instance: adds missing rows, applies recorded revocations,
    /// and advances the version counter past hydrated versions. Memory-only
    /// grants are preserved; revocation only moves toward revoked, never
    /// back. Call before consent-gated runs.</summary>
    public async Task HydrateAsync(string sessionId, IEllaiConsentStore store, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(store);
        var rows = await store.ListBySessionAsync(sessionId, ct).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var row in rows)
            {
                if (_grants.TryGetValue(row.GrantId, out var current))
                {
                    if (row.Revoked && !current.Revoked)
                    {
                        _grants[row.GrantId] = current with { Revoked = true };
                    }
                }
                else
                {
                    _grants[row.GrantId] = row;
                }

                if (row.Version > _version)
                {
                    _version = row.Version;
                }
            }
        }
    }

    public bool IsGranted(string sessionId, EllaiConsentDimension dimension, string purpose, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _grants.Values.Any(g =>
                g.SessionId == sessionId && g.Dimension == dimension
                && string.Equals(g.Purpose, purpose, StringComparison.Ordinal)
                && !g.Revoked && g.ExpiresAtUtc > now);
        }
    }
}
