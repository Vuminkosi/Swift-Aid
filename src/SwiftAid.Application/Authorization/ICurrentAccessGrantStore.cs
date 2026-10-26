using System.Collections.Frozen;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Authorization;

/// <summary>
/// Reads current grants from an authoritative source for each sensitive request.
/// Implementations must not rely only on signed claims or cached role labels.
/// </summary>
public interface ICurrentAccessGrantStore
{
    ValueTask<CurrentAccessGrant?> GetCurrentGrantAsync(
        ActorContext actor,
        CancellationToken cancellationToken);
}

/// <summary>A point-in-time grant snapshot returned by the trusted grant store.</summary>
public sealed class CurrentAccessGrant
{
    public CurrentAccessGrant(
        string actorId,
        long accessEpoch,
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
        IEnumerable<CaseOperation> operations)
    {
        ActorId = RequireIdentifier(actorId, nameof(actorId));
        if (accessEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accessEpoch));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Grant expiry must use UTC.", nameof(expiresAtUtc));
        }

        AccessEpoch = accessEpoch;
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

        if (Sectors.Any(sector => !Enum.IsDefined(sector)))
        {
            throw new ArgumentException("Grant contains an unknown sector.", nameof(sectors));
        }

        if (Operations.Any(operation => !Enum.IsDefined(operation)))
        {
            throw new ArgumentException("Grant contains an unknown operation.", nameof(operations));
        }
    }

    public string ActorId { get; }

    public long AccessEpoch { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public IReadOnlySet<Guid> AssignedCaseIds { get; }

    public IReadOnlySet<Guid> SharedCaseIds { get; }

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
        ArgumentNullException.ThrowIfNull(values);
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
