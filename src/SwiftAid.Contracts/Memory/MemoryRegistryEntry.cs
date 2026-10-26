namespace SwiftAid.Contracts.Memory;

public enum MemoryKind
{
    CaseContext,
    OrganizationContext,
    AccountPreference,
    PublicMetadata
}

public enum MemoryTrustState
{
    Untrusted,
    Reviewed,
    Approved,
    Invalidated
}

/// <summary>Authoritative metadata for a derived memory entry; content remains outside this DTO.</summary>
public sealed record MemoryRegistryEntry(
    Guid MemoryId,
    MemoryKind Kind,
    string? OrganizationId,
    Guid? CaseId,
    string? AccountId,
    string Sector,
    string Sensitivity,
    string Purpose,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<string> SourceVersions,
    MemoryTrustState TrustState,
    string ContentReference,
    string ContentHash,
    DateTimeOffset? ExpiresAtUtc,
    long InvalidationEpoch,
    string IndexStatus);
