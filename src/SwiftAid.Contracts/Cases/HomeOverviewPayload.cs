namespace SwiftAid.Contracts.Cases;

/// <summary>Access outcome for a scoped home overview read.</summary>
public enum HomeOverviewAccess
{
    Allowed,
    Unavailable
}

/// <summary>Assigned-work count kinds in a home overview payload.</summary>
public enum HomeOverviewCountKind
{
    AssignedCases,
    DueFollowUps,
    OverdueFollowUps,
    UnresolvedAssistanceRequests,
    UnresolvedEscalations
}

/// <summary>Why an assigned-work count is not available.</summary>
public enum HomeOverviewCountReason
{
    NotCollected,
    SourceUnavailable
}

/// <summary>One assigned-work count. A null value means the count is unavailable.</summary>
public sealed record HomeOverviewCount(
    HomeOverviewCountKind Kind,
    long? Value,
    HomeOverviewCountReason? Reason);

/// <summary>Recent activity kinds in a home overview payload.</summary>
public enum HomeOverviewActivityKind
{
    CaseReported,
    AssignmentReceived,
    EscalationOpened
}

/// <summary>One recent activity item. Occurrence time never exceeds the payload time.</summary>
public sealed record HomeOverviewActivity(
    string ActivityId,
    HomeOverviewActivityKind Kind,
    DateTimeOffset OccurredAtUtc,
    Guid CaseId);

/// <summary>
/// Privacy-minimized home projection. Counts cover only cases the caller may
/// read; assistance requests are not collected and are always reported with a
/// reason instead of zero.
/// </summary>
public sealed record HomeOverviewPayload(
    string? DisplayName,
    string? WorkspaceLabel,
    DateTimeOffset AsOfUtc,
    DateTimeOffset FreshnessExpiresAtUtc,
    bool Partial,
    bool Truncated,
    IReadOnlyList<HomeOverviewCount> Counts,
    IReadOnlyList<HomeOverviewActivity> Activities);

/// <summary>Home overview read outcome.</summary>
public sealed record HomeOverviewResult(
    HomeOverviewAccess Access,
    HomeOverviewPayload? Payload);
