namespace SwiftAid.UI.Components.Surfaces.Public;

public enum PublicHomeOverviewState
{
    Loading,
    Populated,
    Empty,
    Partial,
    Stale,
    FreshnessUnknown,
    Unavailable,
    Denied
}

public enum PublicHomeCountState
{
    Available,
    Suppressed,
    Unavailable
}

public enum PublicHomeCountKind
{
    Cases,
    FollowUps
}

public sealed record PublicHomeCount(
    PublicHomeCountKind Kind,
    PublicHomeCountState State,
    long? Value);

public enum PublicHomeActivityKind
{
    CaseReported,
    FollowUpCompleted,
    AssignmentReceived,
    AssistanceProvided,
    EscalationOpened
}

public enum PublicHomeActivityDestination
{
    Cases,
    FollowUps,
    Assistance
}

public sealed record PublicHomeActivity(
    PublicHomeActivityKind Kind,
    DateTimeOffset OccurredAtUtc,
    PublicHomeActivityDestination Destination);

/// <summary>Display-only projection; contains no client contract types or opaque record identifiers.</summary>
public sealed record PublicHomeOverview(
    PublicHomeOverviewState State,
    string? VerifiedDisplayName,
    string? AuthorizedWorkspaceLabel,
    string? AuthorizedRegionLabel,
    IReadOnlyList<PublicHomeCount> Counts,
    IReadOnlyList<PublicHomeActivity> RecentActivity);
