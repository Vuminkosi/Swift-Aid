using System.Collections.Frozen;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Authorization;

/// <summary>
/// One coherent scope grant. Authorization must satisfy every resource and operation
/// constraint within this same instance; separate instances are never combined.
/// </summary>
public sealed class ScopedAccessGrant
{
    public ScopedAccessGrant(
        string groupId,
        DateTimeOffset validFromUtc,
        DateTimeOffset expiresAtUtc,
        IEnumerable<Guid> assignedCaseIds,
        IEnumerable<Guid> sharedCaseIds,
        bool canAccessJurisdictionalCases,
        IEnumerable<string> organizationIds,
        IEnumerable<string> workspaceIds,
        IEnumerable<string> countryCodes,
        IEnumerable<string> regionIds,
        IEnumerable<Sector> sectors,
        IEnumerable<string> sensitivityCodes,
        IEnumerable<CaseOperation> operations,
        IReadOnlyDictionary<Guid, IEnumerable<CaseOperation>>? sharedCaseOperations = null)
    {
        GroupId = RequireIdentifier(groupId, nameof(groupId));
        if (validFromUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= validFromUtc)
        {
            throw new ArgumentException("Grant validity must be a non-empty UTC interval.");
        }

        ValidFromUtc = validFromUtc;
        ExpiresAtUtc = expiresAtUtc;
        AssignedCaseIds = Freeze(assignedCaseIds, nameof(assignedCaseIds));
        SharedCaseIds = Freeze(sharedCaseIds, nameof(sharedCaseIds));
        CanAccessJurisdictionalCases = canAccessJurisdictionalCases;
        OrganizationIds = FreezeIdentifiers(organizationIds, nameof(organizationIds));
        WorkspaceIds = FreezeIdentifiers(workspaceIds, nameof(workspaceIds));
        CountryCodes = FreezeIdentifiers(countryCodes, nameof(countryCodes), StringComparer.OrdinalIgnoreCase);
        RegionIds = FreezeIdentifiers(regionIds, nameof(regionIds));
        Sectors = Freeze(sectors, nameof(sectors));
        SensitivityCodes = FreezeIdentifiers(sensitivityCodes, nameof(sensitivityCodes));
        Operations = Freeze(operations, nameof(operations));
        SharedCaseOperations = (sharedCaseOperations ?? new Dictionary<Guid, IEnumerable<CaseOperation>>())
            .ToFrozenDictionary(pair => pair.Key, pair => Freeze(pair.Value, nameof(sharedCaseOperations)));
        if (SharedCaseOperations.Any(pair => !SharedCaseIds.Contains(pair.Key) ||
            pair.Value.Any(operation => !Enum.IsDefined(operation))))
        {
            throw new ArgumentException("Shared action limits must reference an explicit shared case and known operations.", nameof(sharedCaseOperations));
        }

        if (Sectors.Any(sector => !Enum.IsDefined(sector)))
        {
            throw new ArgumentException("Grant contains an unknown sector.", nameof(sectors));
        }

        if (Operations.Any(operation => !Enum.IsDefined(operation)))
        {
            throw new ArgumentException("Grant contains an unknown operation.", nameof(operations));
        }
    }

    public string GroupId { get; }

    public DateTimeOffset ValidFromUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public IReadOnlySet<Guid> AssignedCaseIds { get; }

    public IReadOnlySet<Guid> SharedCaseIds { get; }

    /// <summary>Per-case action limits for bounded shares. Absent entries retain legacy share behavior.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlySet<CaseOperation>> SharedCaseOperations { get; }

    public bool CanAccessJurisdictionalCases { get; }

    public IReadOnlySet<string> OrganizationIds { get; }

    public IReadOnlySet<string> WorkspaceIds { get; }

    public IReadOnlySet<string> CountryCodes { get; }

    public IReadOnlySet<string> RegionIds { get; }

    public IReadOnlySet<Sector> Sectors { get; }

    public IReadOnlySet<string> SensitivityCodes { get; }

    public IReadOnlySet<CaseOperation> Operations { get; }

    private static IReadOnlySet<T> Freeze<T>(IEnumerable<T> values, string parameterName) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return values.ToFrozenSet();
    }

    private static IReadOnlySet<string> FreezeIdentifiers(
        IEnumerable<string> values,
        string parameterName,
        StringComparer? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.ToArray();
        if (snapshot.Any(value => string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException("Grant scope contains an empty or non-canonical identifier.", parameterName);
        }

        return snapshot.ToFrozenSet(comparer ?? StringComparer.Ordinal);
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
