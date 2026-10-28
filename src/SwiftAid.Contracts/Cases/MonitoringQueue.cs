namespace SwiftAid.Contracts.Cases;

/// <summary>Kind of open monitoring work in the cross-case queue.</summary>
public enum MonitoringQueueKind
{
    FollowUp,
    Escalation
}

/// <summary>
/// Minimum-necessary queue row for one open follow-up or raised escalation.
/// Carries scheduling identity and outcome state only; narratives and sealed
/// payloads stay on the per-case timeline behind ReadCase.
/// </summary>
public sealed record MonitoringQueueItem(
    Guid CaseId,
    MonitoringQueueKind Kind,
    Guid WorkItemId,
    string Summary,
    string? AssigneeActorId,
    DateTimeOffset? DueAtUtc,
    string Status,
    bool IsOverdue,
    DateTimeOffset RecordedAtUtc,
    long AggregateVersion);

/// <summary>Monitoring queue scope: the caller's assigned work, or all open work they may read.</summary>
public enum MonitoringQueueScope
{
    Assigned,
    Open
}

/// <summary>Monitoring queue result: due-ordered open work, or an empty list with a flag.</summary>
public sealed record MonitoringQueueResult(
    CaseQueueAccess Access,
    IReadOnlyList<MonitoringQueueItem> Items);

/// <summary>Staff monitoring queue client. Servers enforce ReadCase per case; clients only render.</summary>
public interface IMonitoringQueueClient
{
    Task<MonitoringQueueResult> GetAssignedAsync(int? limit, CancellationToken cancellationToken = default);

    Task<MonitoringQueueResult> GetOpenAsync(int? limit, CancellationToken cancellationToken = default);
}
