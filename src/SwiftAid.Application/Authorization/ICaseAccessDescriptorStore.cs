namespace SwiftAid.Application.Authorization;

/// <summary>
/// Resolves minimized case authorization metadata from an authoritative server-side source.
/// Implementations must ignore request filters and return the current stored resource scope.
/// </summary>
public interface ICaseAccessDescriptorStore
{
    ValueTask<CaseAccessDescriptor?> GetCurrentDescriptorAsync(
        Guid caseId,
        CancellationToken cancellationToken);
}
