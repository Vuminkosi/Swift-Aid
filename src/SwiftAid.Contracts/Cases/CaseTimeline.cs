using System.Text.Json.Serialization;

namespace SwiftAid.Contracts.Cases;

/// <summary>How a timeline entry was produced. Only authoritative journal rows move case state.</summary>
public enum CaseTimelineEntryKind
{
    Authoritative,
    Derived
}

/// <summary>
/// One immutable history row. Reasons and report content stay sealed; this projection
/// carries only event metadata, a safe authorized actor display label when available,
/// timestamps, and version references. ActorId remains an internal opaque identifier.
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
    int SchemaVersion,
    string? ActorDisplayLabel = null)
{
    /// <summary>
    /// Returns only a bounded display label supplied by an authorized projection.
    /// The opaque ActorId must never be used as a UI fallback.
    /// </summary>
    [JsonIgnore]
    public string? SafeActorDisplayLabel =>
        ActorDisplayLabel is { Length: > 0 and <= 120 } label &&
        !label.Any(char.IsControl)
            ? label
            : null;
}

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
    DateTimeOffset CreatedAtUtc,
    long? AssignmentVersion = null,
    DateTimeOffset? AssignmentAcknowledgedAtUtc = null,
    bool CanAcknowledgeAssignment = false,
    DateTimeOffset? AssignmentRecordedAtUtc = null);

/// <summary>Ordered oldest-first immutable history for one managed case.</summary>
public sealed record CaseTimeline(
    CaseTimelineHeader Header,
    IReadOnlyList<CaseTimelineEntry> Entries,
    bool HasOlderEntries = false,
    long? OlderThanAggregateVersion = null);

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
    Task<CaseTimelineResult> GetAsync(
        Guid caseId,
        int? limit,
        CancellationToken cancellationToken = default,
        long? beforeAggregateVersion = null);
}
