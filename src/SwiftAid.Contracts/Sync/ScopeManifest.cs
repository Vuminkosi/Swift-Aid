namespace SwiftAid.Contracts.Sync;

/// <summary>Minimized, versioned synchronization scope for the current authenticated session.</summary>
public sealed record ScopeManifest
{
    public ScopeManifest(
        IEnumerable<string> organizationIds,
        IEnumerable<string> workspaceIds,
        IEnumerable<Guid> caseIds,
        IEnumerable<string> countryCodes,
        IEnumerable<string> regionIds,
        long accessEpoch,
        DateTimeOffset expiresAtUtc)
    {
        if (accessEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accessEpoch));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Scope expiry must use UTC.", nameof(expiresAtUtc));
        }

        OrganizationIds = SnapshotIdentifiers(organizationIds, nameof(organizationIds));
        WorkspaceIds = SnapshotIdentifiers(workspaceIds, nameof(workspaceIds));
        CaseIds = Snapshot(caseIds, nameof(caseIds));
        CountryCodes = SnapshotIdentifiers(countryCodes, nameof(countryCodes));
        RegionIds = SnapshotIdentifiers(regionIds, nameof(regionIds));
        AccessEpoch = accessEpoch;
        ExpiresAtUtc = expiresAtUtc;
    }

    public IReadOnlyList<string> OrganizationIds { get; }

    public IReadOnlyList<string> WorkspaceIds { get; }

    public IReadOnlyList<Guid> CaseIds { get; }

    public IReadOnlyList<string> CountryCodes { get; }

    public IReadOnlyList<string> RegionIds { get; }

    public long AccessEpoch { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return Array.AsReadOnly(values.Distinct().OrderBy(value => value).ToArray());
    }

    private static IReadOnlyList<string> SnapshotIdentifiers(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.ToArray();
        if (snapshot.Any(value => string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException("Scope contains an empty or non-canonical identifier.", parameterName);
        }

        return Array.AsReadOnly(snapshot.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}
