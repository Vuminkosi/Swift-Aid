using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Intake;

/// <summary>
/// Pure reviewer-decision transition rules for one intake report. Persistence stays in
/// <c>PostgresIntakeDecisionStore</c>; this validator carries no database, clock, or actor
/// dependency so the rules are unit-testable without PostgreSQL.
/// </summary>
/// <remarks>
/// Layering: the store returns <c>NotFoundOrNotAuthorized</c> when the receipt does not
/// exist. Actor authorization (catchment reviewer) is enforced by the staff endpoint that
/// calls <c>IIntakeService.DecideAsync</c>, not by this validator. <c>AcceptIntoCase</c>
/// throws <see cref="IntakePromotionUnavailableException"/> because the unique promotion
/// link must be atomic with the first managed-case events (spec §6), and the managed-case
/// registration execution backend has no production implementation yet.
/// </remarks>
public static class IntakeDecisionTransition
{
    /// <summary>
    /// Evaluates a reviewer decision against the current report state. Returns the decision
    /// status and, when <c>Applied</c>, the status the store must persist with an incremented
    /// version. Never returns a new status for <c>Conflict</c>, <c>Invalid</c>, or
    /// <c>NotFoundOrNotAuthorized</c> callers.
    /// </summary>
    /// <exception cref="IntakePromotionUnavailableException">
    /// Thrown for <c>AcceptIntoCase</c>; promotion execution is unavailable.
    /// </exception>
    public static (IntakeDecisionStatus Status, IntakeStatus? NewStatus) Evaluate(
        IntakeDecision command,
        IntakeStatus currentStatus,
        long currentVersion,
        IntakeRoutingStatus routingStatus,
        string? intakeQueueId)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(command.Decision) || !Enum.IsDefined(currentStatus) ||
            !Enum.IsDefined(routingStatus) || command.ExpectedVersion < 1 || currentVersion < 1)
        {
            return (IntakeDecisionStatus.Invalid, null);
        }

        if (command.ExpectedVersion != currentVersion)
        {
            return (IntakeDecisionStatus.Conflict, null);
        }

        if (IsTerminal(currentStatus))
        {
            return command.Decision == ToDecisionKind(currentStatus)
                ? (IntakeDecisionStatus.Applied, currentStatus)
                : (IntakeDecisionStatus.Conflict, null);
        }

        switch (command.Decision)
        {
            case IntakeDecisionKind.RouteForReview:
                if (routingStatus != IntakeRoutingStatus.RoutedForReview ||
                    string.IsNullOrWhiteSpace(intakeQueueId))
                {
                    return (IntakeDecisionStatus.Invalid, null);
                }

                return (IntakeDecisionStatus.Applied, IntakeStatus.AwaitingReview);
            case IntakeDecisionKind.Decline:
                return (IntakeDecisionStatus.Applied, IntakeStatus.Declined);
            case IntakeDecisionKind.LinkAsEvidence:
                // Target-case binding arrives with the managed-case linkage slice; the status
                // transition reserves the report so it cannot be decided twice.
                return (IntakeDecisionStatus.Applied, IntakeStatus.LinkedAsEvidence);
            case IntakeDecisionKind.AcceptIntoCase:
                throw new IntakePromotionUnavailableException();
            default:
                return (IntakeDecisionStatus.Invalid, null);
        }
    }

    private static bool IsTerminal(IntakeStatus status) =>
        status is IntakeStatus.AcceptedIntoCase or IntakeStatus.LinkedAsEvidence or IntakeStatus.Declined;

    private static IntakeDecisionKind ToDecisionKind(IntakeStatus status) =>
        status switch
        {
            IntakeStatus.AcceptedIntoCase => IntakeDecisionKind.AcceptIntoCase,
            IntakeStatus.LinkedAsEvidence => IntakeDecisionKind.LinkAsEvidence,
            IntakeStatus.Declined => IntakeDecisionKind.Decline,
            _ => throw new ArgumentOutOfRangeException(nameof(status), "The report status is not a terminal decision."),
        };
}
