using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Intake;

/// <summary>Versioned coordinator command; the receipt identity travels with the call.</summary>
public sealed record IntakeCoordinationCommand(
    Guid ReceiptId,
    long ExpectedVersion,
    IntakeCoordinationAction Action,
    string? TargetQueueId,
    string? Reason);

/// <summary>Proven coordinator authority: actor plus the platform grant that grants it.</summary>
public sealed record IntakeCoordinationAuthority(
    string ActorId,
    string CoordinationOrganizationId,
    string GrantGroupId);

/// <summary>Determines whether a staff actor holds platform coordination authority.</summary>
/// <remarks>
/// Authority derives only from an explicit platform coordination grant in the configured
/// coordination organization. Geographic proximity never grants access, and routed
/// reports (which belong to organization reviewers) are never coordinator-visible.
/// </remarks>
public interface IIntakeCoordinationAuthorizer
{
    /// <summary>Platform coordinator check without report scoping (queue listing).</summary>
    Task<IntakeCoordinationAuthority?> AuthorizeAsync(
        ActorContext actor,
        CancellationToken ct);

    /// <summary>
    /// Report-scoped check: the actor must be a platform coordinator and the receipt
    /// must be an unrouted, non-terminal report.
    /// </summary>
    Task<IntakeCoordinationAuthority?> AuthorizeReportAsync(
        ActorContext actor,
        Guid receiptId,
        CancellationToken ct);
}

/// <summary>Coordinator queue reads and versioned, audited coordination writes.</summary>
public interface IIntakeCoordinationStore
{
    Task<IReadOnlyList<IntakeCoordinationQueueEntry>> GetQueueAsync(
        IntakeRoutingStatus? routingStatus,
        int limit,
        DateTimeOffset? receivedBeforeUtc,
        Guid? receiptBefore,
        CancellationToken ct);

    Task<IntakeCoordinationDetail?> GetDetailAsync(
        Guid receiptId,
        CancellationToken ct);

    /// <summary>Currently eligible assignment targets (active configured route queues).</summary>
    Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetEligibleQueuesAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct);

    /// <summary>
    /// Applies one versioned coordination action under the receipt lock and records the
    /// audit row atomically. Returns <c>Conflict</c> on version drift and
    /// <c>NotFoundOrNotAuthorized</c> when the receipt is missing, routed, or terminal.
    /// </summary>
    Task<IntakeDecisionResult> ApplyAsync(
        IntakeCoordinationCommand command,
        IntakeCoordinationAuthority authority,
        DateTimeOffset nowUtc,
        CancellationToken ct);
}

/// <summary>Coordinator-facing queue service: authorization first, then the store.</summary>
public interface IIntakeCoordinationService
{
    Task<IReadOnlyList<IntakeCoordinationQueueEntry>> GetQueueAsync(
        ActorContext actor,
        IntakeRoutingStatus? routingStatus,
        int limit,
        DateTimeOffset? receivedBeforeUtc,
        Guid? receiptBefore,
        CancellationToken ct);

    Task<IntakeCoordinationDetail?> GetDetailAsync(
        ActorContext actor,
        Guid receiptId,
        CancellationToken ct);

    Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetEligibleQueuesAsync(
        ActorContext actor,
        CancellationToken ct);

    Task<IntakeDecisionResult> ActAsync(
        IntakeCoordinationCommand command,
        ActorContext actor,
        CancellationToken ct);
}
