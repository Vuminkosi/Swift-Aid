namespace SwiftAid.Contracts.Cases;

/// <summary>One page of work for a single authorized case and aggregate version.</summary>
public sealed record CaseMonitoringPage(
    Guid CaseId,
    long AggregateVersion,
    IReadOnlyList<CaseMonitoringPageItem> Items,
    int? NextOffset);

public sealed record CaseMonitoringPageItem(MonitoringQueueItem Work, bool AssignedToCurrentActor);

public enum CaseMonitoringPageAccess
{
    Found,
    NotFoundOrNotAuthorized,
    Stale,
    Unavailable
}

public sealed record CaseMonitoringPageResult(CaseMonitoringPageAccess Access, CaseMonitoringPage? Page);

public interface ICaseMonitoringPageClient
{
    Task<CaseMonitoringPageResult> GetAsync(Guid caseId, int offset, int limit, long version,
        CancellationToken cancellationToken = default);
}
