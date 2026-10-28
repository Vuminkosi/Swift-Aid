namespace SwiftAid.Contracts.Intake;

/// <summary>PC-09 restricted coordination actions on unrouted guest intake reports.</summary>
/// <remarks>
/// Coordinators never accept, link, or decline reports: those reviewer decisions stay on
/// the organization review path. <c>AssignQueue</c> corrects routing metadata by moving the
/// report into a configured, currently eligible organization queue; the organization
/// reviewer then owns acceptance. <c>ClaimForReview</c> is self-only.
/// </remarks>
public enum IntakeCoordinationAction
{
    ClaimForReview,
    AssignQueue,
    RequestClarification,
    RecordNoServiceCoverage
}

/// <summary>Minimum-necessary coordination projection: routing assessment fields only.</summary>
/// <remarks>
/// Narrative, observations, precise coordinates, and original-audio references are
/// deliberately absent. Coordinator endpoints never decrypt the sealed submission.
/// </remarks>
public sealed record IntakeCoordinationQueueEntry(
    Guid ReceiptId,
    IntakeStatus Status,
    IntakeRoutingStatus RoutingStatus,
    string? IntakeQueueId,
    string CountryCode,
    string? RegionId,
    string? AreaId,
    string? CatalogueVersion,
    IntakeSubjectScope SubjectScope,
    DateTimeOffset ReceivedAtUtc,
    long Version,
    string? CoordinatorActorId,
    bool ClarificationRequested);

/// <summary>One recorded coordination action: actor, authority, reason, time, result.</summary>
public sealed record IntakeCoordinationActionRecord(
    Guid ActionId,
    IntakeCoordinationAction Action,
    string ActorId,
    string CoordinationOrganizationId,
    string GrantGroupId,
    string? Reason,
    IntakeStatus FromStatus,
    IntakeRoutingStatus FromRouting,
    string? FromQueueId,
    IntakeStatus ToStatus,
    IntakeRoutingStatus ToRouting,
    string? ToQueueId,
    long ResultingVersion,
    DateTimeOffset ActedAtUtc);

/// <summary>Coordination detail: redacted report projection plus its action history.</summary>
public sealed record IntakeCoordinationDetail(
    IntakeCoordinationQueueEntry Report,
    IReadOnlyList<IntakeCoordinationActionRecord> History);

/// <summary>An eligible assignment target: a currently active configured route queue.</summary>
public sealed record IntakeCoordinationQueueOption(
    string QueueId,
    string ReceivingOrganizationId,
    string CountryCode,
    string? CatalogueVersion);

/// <summary>Wire-only coordinator action request; receipt identity travels in the route.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record IntakeCoordinationActionRequest(
    int SchemaVersion,
    long ExpectedVersion,
    string? Action,
    string? TargetQueueId,
    string? Reason);

/// <summary>Queue read outcome distinguishing denial/unavailability from an empty queue.</summary>
public enum CoordinationQueueAccess
{
    Allowed,
    Denied,
    Unavailable
}

/// <summary>Coordinator action outcome for user feedback.</summary>
public enum CoordinationActionOutcome
{
    Applied,
    Conflict,
    Invalid,
    NotFound,
    Unavailable
}

/// <summary>Client-facing coordination queue contract shared by web and mobile hosts.</summary>
public interface IIntakeCoordinationClient
{
    Task<(CoordinationQueueAccess Access, IReadOnlyList<IntakeCoordinationQueueEntry> Entries)> GetQueueAsync(
        IntakeRoutingStatus? routingStatus,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IntakeCoordinationDetail?> GetDetailAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetQueuesAsync(
        CancellationToken cancellationToken = default);

    Task<CoordinationActionOutcome> ActAsync(
        Guid receiptId,
        long expectedVersion,
        IntakeCoordinationAction action,
        string? targetQueueId,
        string reason,
        CancellationToken cancellationToken = default);
}
