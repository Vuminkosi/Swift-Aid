using System.Collections.Frozen;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Authorization;

/// <summary>
/// Minimized resource metadata supplied by a trusted server-side repository.
/// It must not be constructed from client filters or request-body scope claims.
/// </summary>
public sealed class CaseAccessDescriptor
{
    public CaseAccessDescriptor(
        Guid caseId,
        long resourceVersion,
        string organizationId,
        string workspaceId,
        string countryCode,
        IEnumerable<string> regionIds,
        Sector sector,
        string sensitivityCode)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("A case identifier is required.", nameof(caseId));
        }

        if (resourceVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceVersion));
        }

        CaseId = caseId;
        ResourceVersion = resourceVersion;
        OrganizationId = RequireIdentifier(organizationId, nameof(organizationId));
        WorkspaceId = RequireIdentifier(workspaceId, nameof(workspaceId));
        CountryCode = RequireIdentifier(countryCode, nameof(countryCode)).ToUpperInvariant();
        RegionIds = FreezeIdentifiers(regionIds, nameof(regionIds));

        if (!Enum.IsDefined(sector))
        {
            throw new ArgumentOutOfRangeException(nameof(sector));
        }

        Sector = sector;
        SensitivityCode = RequireIdentifier(sensitivityCode, nameof(sensitivityCode));
    }

    public Guid CaseId { get; }

    public long ResourceVersion { get; }

    public string OrganizationId { get; }

    public string WorkspaceId { get; }

    public string CountryCode { get; }

    public IReadOnlySet<string> RegionIds { get; }

    public Sector Sector { get; }

    /// <summary>An opaque sensitivity label; no clinical condition text is carried here.</summary>
    public string SensitivityCode { get; }

    private static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A non-empty canonical identifier is required.", parameterName);
        }

        return value;
    }

    private static IReadOnlySet<string> FreezeIdentifiers(IEnumerable<string> identifiers, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        var values = identifiers.ToArray();
        if (values.Any(value => string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException("Scope identifiers must be non-empty canonical values.", parameterName);
        }

        return values.ToFrozenSet(StringComparer.Ordinal);
    }
}
