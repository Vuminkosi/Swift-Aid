namespace SwiftAid.Contracts.Cases;

/// <summary>Case discovery by open work kind; every returned case has been authorized.</summary>
public enum CaseWorkDiscoveryKind { FollowUp, Escalation, Assistance }

public enum CaseWorkDiscoveryScope { Open, Assigned }

/// <summary>The cursor is the last returned case, not an access grant.</summary>
public sealed record CaseWorkDiscoveryPage(CaseQueueAccess Access, IReadOnlyList<Guid> CaseIds, Guid? NextCursor = null);

public interface ICaseWorkDiscoveryClient
{
    Task<CaseWorkDiscoveryPage> GetAsync(CaseWorkDiscoveryKind kind, CaseWorkDiscoveryScope scope,
        int limit, Guid? cursor, CancellationToken cancellationToken = default);
}
