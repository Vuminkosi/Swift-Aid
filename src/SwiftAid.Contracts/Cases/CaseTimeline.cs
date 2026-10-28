namespace SwiftAid.Contracts.Cases;

/// <summary>How a timeline entry was produced. Only authoritative journal rows move case state.</summary>
public enum CaseTimelineEntryKind
{
    Authoritative,
    Derived
}

/// <summary>
/// One immutable history row. Reasons and report content stay sealed; this projection
/// carries only who recorded what kind of event, when, and at which version.
/// </summary>
public sealed record CaseTimelineEntry(
    Guid EventId,
    string EventType,
    string Label,
    CaseTimelineEntryKind Kind,
    long AggregateVersion,
    long ReportRevision,
    Guid CommandId,
    string ActorId,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    int SchemaVersion);

/// <summary>Minimum-necessary case header for the timeline view. No report content.</summary>
public sealed record CaseTimelineHeader(
    Guid CaseId,
    string OrganizationId,
    string WorkspaceId,
    string CountryCode,
    string Workflow,
    string AccountableOwnerActorId,
    long AggregateVersion,
    long ReportRevision,
    DateTimeOffset CreatedAtUtc);

/// <summary>Ordered oldest-first immutable history for one managed case.</summary>
public sealed record CaseTimeline(
    CaseTimelineHeader Header,
    IReadOnlyList<CaseTimelineEntry> Entries);

/// <summary>Timeline read outcome; denial and absence share one value for privacy.</summary>
public enum CaseTimelineAccess
{
    Found,
    NotFoundOrNotAuthorized
}

public sealed record CaseTimelineResult(
    CaseTimelineAccess Access,
    CaseTimeline? Timeline);

/// <summary>Staff timeline client. Servers enforce ReadCase; clients only render.</summary>
public interface ICaseTimelineClient
{
    Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default);
}
