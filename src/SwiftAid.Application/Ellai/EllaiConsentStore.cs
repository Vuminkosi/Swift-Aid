namespace SwiftAid.Application.Ellai;

/// <summary>Durable consent-grant port. PostgreSQL is the production
/// implementation; memory holds the hot path. Grants are immutable except
/// the revocation flag; versions only move forward.</summary>
public interface IEllaiConsentStore
{
    Task RecordAsync(EllaiConsentGrant grant, CancellationToken ct);

    Task<IReadOnlyList<EllaiConsentGrant>> ListBySessionAsync(string sessionId, CancellationToken ct);

    /// <summary>Marks one session grant revoked. False when the row is absent.</summary>
    Task<bool> SetRevokedAsync(string sessionId, string grantId, CancellationToken ct);
}

/// <summary>Thread-safe in-memory consent store for tests and local
/// development without a database. A restarted process loses unpersisted
/// rows; pair with hydration from the durable store on startup and runs.</summary>
public sealed class InMemoryEllaiConsentStore : IEllaiConsentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiConsentGrant> _grants = new(StringComparer.Ordinal);

    public Task RecordAsync(EllaiConsentGrant grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            _grants.TryAdd(grant.GrantId, grant);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EllaiConsentGrant>> ListBySessionAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EllaiConsentGrant>>(
                _grants.Values.Where(g => g.SessionId == sessionId).ToList());
        }
    }

    public Task<bool> SetRevokedAsync(string sessionId, string grantId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_grants.TryGetValue(grantId, out var grant) || grant.SessionId != sessionId)
            {
                return Task.FromResult(false);
            }

            _grants[grantId] = grant with { Revoked = true };
            return Task.FromResult(true);
        }
    }
}
