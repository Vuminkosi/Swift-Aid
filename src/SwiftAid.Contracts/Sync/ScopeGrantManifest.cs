namespace SwiftAid.Contracts.Sync;

/// <summary>A minimized sync scope entry that preserves one grant's correlation.</summary>
public sealed record ScopeGrantManifest
{
    public ScopeGrantManifest(
        string groupId,
        DateTimeOffset validFromUtc,
        DateTimeOffset expiresAtUtc,
        IEnumerable<string> organizationIds,
        IEnumerable<string> workspaceIds,
        IEnumerable<Guid> caseIds,
        IEnumerable<string> countryCodes,
        IEnumerable<string> regionIds,
        IEnumerable<string> sensitivityCodes,
        IEnumerable<string> sectorCodes,
        IEnumerable<string> operations,
        bool canAccessJurisdictionalCases)
    {
        GroupId = RequireIdentifier(groupId, nameof(groupId));
        if (validFromUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= validFromUtc)
        {
            throw new ArgumentException("Scope grant validity must be a non-empty UTC interval.");
        }

        ValidFromUtc = validFromUtc;
        ExpiresAtUtc = expiresAtUtc;
        OrganizationIds = SnapshotIdentifiers(organizationIds, nameof(organizationIds));
        WorkspaceIds = SnapshotIdentifiers(workspaceIds, nameof(workspaceIds));
        CaseIds = Snapshot(caseIds, nameof(caseIds));
        CountryCodes = SnapshotIdentifiers(countryCodes, nameof(countryCodes));
        RegionIds = SnapshotIdentifiers(regionIds, nameof(regionIds));
        SensitivityCodes = SnapshotIdentifiers(
            SnapshotSensitivityCodes(sensitivityCodes, nameof(sensitivityCodes)), nameof(sensitivityCodes));
        SectorCodes = SnapshotIdentifiers(sectorCodes, nameof(sectorCodes));
        Operations = SnapshotIdentifiers(operations, nameof(operations));
        CanAccessJurisdictionalCases = canAccessJurisdictionalCases;
    }

    public string GroupId { get; }

    public DateTimeOffset ValidFromUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public IReadOnlyList<string> OrganizationIds { get; }

    public IReadOnlyList<string> WorkspaceIds { get; }

    public IReadOnlyList<Guid> CaseIds { get; }

    public IReadOnlyList<string> CountryCodes { get; }

    public IReadOnlyList<string> RegionIds { get; }

    /// <summary>Normalized authorization-category codes; excludes condition or narrative data.</summary>
    public IReadOnlyList<string> SensitivityCodes { get; }

    public IReadOnlyList<string> SectorCodes { get; }

    public IReadOnlyList<string> Operations { get; }

    public bool CanAccessJurisdictionalCases { get; }

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

    private static IEnumerable<string> SnapshotSensitivityCodes(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Sensitivity categories must be non-empty identifiers.", parameterName);
        }

        return snapshot.Select(value => value.Trim());
    }

    private static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A non-empty canonical identifier is required.", parameterName);
        }

        return value;
    }
}
