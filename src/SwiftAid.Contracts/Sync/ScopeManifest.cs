namespace SwiftAid.Contracts.Sync;

/// <summary>Version 2 minimized sync scope preserving authoritative grant correlation.</summary>
public sealed record ScopeManifest
{
    public const int CurrentSchemaVersion = 2;

    public ScopeManifest(
        IEnumerable<ScopeGrantManifest> grants,
        long accessEpoch,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(grants);
        if (accessEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accessEpoch));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Scope expiry must use UTC.", nameof(expiresAtUtc));
        }

        var snapshot = grants.ToArray();
        if (snapshot.Any(grant => grant is null))
        {
            throw new ArgumentException("Scope grant groups cannot contain null entries.", nameof(grants));
        }

        if (snapshot.Select(grant => grant.GroupId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Scope grant group identifiers must be unique.", nameof(grants));
        }

        SchemaVersion = CurrentSchemaVersion;
        Grants = Array.AsReadOnly(snapshot);
        AccessEpoch = accessEpoch;
        ExpiresAtUtc = expiresAtUtc;
    }

    public int SchemaVersion { get; }

    public IReadOnlyList<ScopeGrantManifest> Grants { get; }

    public long AccessEpoch { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}
