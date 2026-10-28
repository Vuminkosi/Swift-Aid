using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Intake;

/// <summary>
/// Pure coordinator-action transition rules for unrouted intake reports. Persistence stays
/// in the coordination store; this validator carries no database, clock, or actor
/// dependency so the rules are unit-testable without PostgreSQL.
/// </summary>
/// <remarks>
/// Layering: the store returns <c>NotFoundOrNotAuthorized</c> when the receipt is missing,
/// routed, or terminal. Coordinator authorization (platform coordination grant) is enforced
/// by the coordination endpoint and service, not by this validator. Queue eligibility
/// (configured, currently active route) is enforced by the store against
/// <c>intake_routes</c>; this validator checks only the command shape and lifecycle.
/// </remarks>
public static class IntakeCoordinationTransition
{
    /// <summary>
    /// Evaluates a coordinator command against the current report state. Returns the action
    /// status and, when <c>Applied</c>, the values the store must persist. Never returns new
    /// state for <c>Conflict</c>, <c>Invalid</c>, or <c>NotFoundOrNotAuthorized</c> callers.
    /// </summary>
    public static (IntakeDecisionStatus Status, IntakeCoordinationEffect? Effect) Evaluate(
        IntakeCoordinationCommand command,
        IntakeStatus currentStatus,
        long currentVersion,
        IntakeRoutingStatus routingStatus,
        string? intakeQueueId,
        string? coordinatorActorId,
        bool clarificationRequested)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(command.Action) || !Enum.IsDefined(currentStatus) ||
            !Enum.IsDefined(routingStatus) || command.ReceiptId == Guid.Empty ||
            command.ExpectedVersion < 1 || currentVersion < 1)
        {
            return (IntakeDecisionStatus.Invalid, null);
        }

        if (command.ExpectedVersion != currentVersion)
        {
            return (IntakeDecisionStatus.Conflict, null);
        }

        if (IsTerminal(currentStatus))
        {
            return (IntakeDecisionStatus.Conflict, null);
        }

        if (!IsUnrouted(routingStatus, intakeQueueId))
        {
            // Routed reports belong to organization reviewers; coordinators lose visibility.
            return (IntakeDecisionStatus.NotFoundOrNotAuthorized, null);
        }

        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 1000)
        {
            return (IntakeDecisionStatus.Invalid, null);
        }

        return command.Action switch
        {
            IntakeCoordinationAction.ClaimForReview =>
                EvaluateClaim(command, coordinatorActorId),
            IntakeCoordinationAction.AssignQueue =>
                EvaluateAssign(command, currentStatus, intakeQueueId),
            IntakeCoordinationAction.RequestClarification =>
                EvaluateClarification(command),
            IntakeCoordinationAction.RecordNoServiceCoverage =>
                EvaluateNoCoverage(command, routingStatus),
            _ => (IntakeDecisionStatus.Invalid, null),
        };
    }

    private static (IntakeDecisionStatus, IntakeCoordinationEffect?) EvaluateClaim(
        IntakeCoordinationCommand command,
        string? coordinatorActorId)
    {
        if (command.TargetQueueId is not null)
        {
            return (IntakeDecisionStatus.Invalid, null);
        }

        // Claiming is self-only; the service binds the acting coordinator.
        return (IntakeDecisionStatus.Applied, new IntakeCoordinationEffect(
            AssignCoordinator: true,
            NextStatus: null,
            NextRouting: null,
            NextQueueId: null,
            NextQueueIdSet: false,
            SetClarification: null));
    }

    private static (IntakeDecisionStatus, IntakeCoordinationEffect?) EvaluateAssign(
        IntakeCoordinationCommand command,
        IntakeStatus currentStatus,
        string? intakeQueueId)
    {
        if (string.IsNullOrWhiteSpace(command.TargetQueueId) ||
            command.TargetQueueId.Length > 128 ||
            string.Equals(command.TargetQueueId, intakeQueueId, StringComparison.Ordinal))
        {
            return (IntakeDecisionStatus.Invalid, null);
        }

        if (currentStatus != IntakeStatus.ReceivedUnverified)
        {
            return (IntakeDecisionStatus.Invalid, null);
        }

        return (IntakeDecisionStatus.Applied, new IntakeCoordinationEffect(
            AssignCoordinator: false,
            NextStatus: IntakeStatus.AwaitingReview,
            NextRouting: IntakeRoutingStatus.RoutedForReview,
            NextQueueId: command.TargetQueueId,
            NextQueueIdSet: true,
            SetClarification: null));
    }

    private static (IntakeDecisionStatus, IntakeCoordinationEffect?) EvaluateClarification(
        IntakeCoordinationCommand command)
    {
        if (command.TargetQueueId is not null)
        {
            return (IntakeDecisionStatus.Invalid, null);
        }

        return (IntakeDecisionStatus.Applied, new IntakeCoordinationEffect(
            AssignCoordinator: false,
            NextStatus: null,
            NextRouting: null,
            NextQueueId: null,
            NextQueueIdSet: false,
            SetClarification: true));
    }

    private static (IntakeDecisionStatus, IntakeCoordinationEffect?) EvaluateNoCoverage(
        IntakeCoordinationCommand command,
        IntakeRoutingStatus routingStatus)
    {
        if (command.TargetQueueId is not null)
        {
            return (IntakeDecisionStatus.Invalid, null);
        }

        if (routingStatus != IntakeRoutingStatus.NeedsRouting)
        {
            // Re-affirming an already-recorded uncovered status is a no-op replay.
            return (IntakeDecisionStatus.Applied, IntakeCoordinationEffect.NoChange);
        }

        return (IntakeDecisionStatus.Applied, new IntakeCoordinationEffect(
            AssignCoordinator: false,
            NextStatus: null,
            NextRouting: IntakeRoutingStatus.NoServiceCoverage,
            NextQueueId: null,
            NextQueueIdSet: false,
            SetClarification: null));
    }

    private static bool IsTerminal(IntakeStatus status) =>
        status is IntakeStatus.AcceptedIntoCase or IntakeStatus.LinkedAsEvidence or IntakeStatus.Declined;

    private static bool IsUnrouted(IntakeRoutingStatus routing, string? queueId) =>
        queueId is null &&
        routing is IntakeRoutingStatus.NeedsRouting or IntakeRoutingStatus.NoServiceCoverage;
}

/// <summary>Store mutation described by the transition validator.</summary>
/// <param name="AssignCoordinator">Bind the acting coordinator to the report.</param>
/// <param name="NextStatus">Lifecycle target, or <c>null</c> to keep the current status.</param>
/// <param name="NextRouting">Routing target, or <c>null</c> to keep the current routing.</param>
/// <param name="NextQueueId">Queue target; honored only when <paramref name="NextQueueIdSet"/> is set.</param>
/// <param name="NextQueueIdSet">Whether the queue assignment changes.</param>
/// <param name="SetClarification">Whether to raise the clarification flag, or <c>null</c> to keep it.</param>
public sealed record IntakeCoordinationEffect(
    bool AssignCoordinator,
    IntakeStatus? NextStatus,
    IntakeRoutingStatus? NextRouting,
    string? NextQueueId,
    bool NextQueueIdSet,
    bool? SetClarification)
{
    /// <summary>No-op replay that still records the audit row.</summary>
    public static IntakeCoordinationEffect NoChange { get; } = new(
        false, null, null, null, false, null);
}
