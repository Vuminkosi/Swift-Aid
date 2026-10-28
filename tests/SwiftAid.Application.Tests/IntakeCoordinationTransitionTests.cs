using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Tests;

public sealed class IntakeCoordinationTransitionTests
{
    [Fact]
    public void Claim_AppliesAndBindsCoordinator()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.ClaimForReview, null, "Taking review.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.NotNull(effect);
        Assert.True(effect.AssignCoordinator);
        Assert.Null(effect.NextStatus);
        Assert.Null(effect.NextRouting);
        Assert.False(effect.NextQueueIdSet);
    }

    [Fact]
    public void Claim_WithTargetQueue_IsInvalid()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.ClaimForReview, "queue-a", "Taking review.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Invalid, status);
        Assert.Null(effect);
    }

    [Fact]
    public void Assign_MovesUnverifiedReportIntoReview()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 2, IntakeCoordinationAction.AssignQueue, "queue-zw-harare", "Catchment confirmed.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 2,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.NotNull(effect);
        Assert.Equal(IntakeStatus.AwaitingReview, effect.NextStatus);
        Assert.Equal(IntakeRoutingStatus.RoutedForReview, effect.NextRouting);
        Assert.True(effect.NextQueueIdSet);
        Assert.Equal("queue-zw-harare", effect.NextQueueId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Assign_WithoutTargetQueue_IsInvalid(string? target)
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.AssignQueue, target, "Catchment confirmed.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Invalid, status);
        Assert.Null(effect);
    }

    [Fact]
    public void Assign_SameQueue_IsInvalid()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.AssignQueue, "queue-a", "Reassign.");

        var (status, _) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1,
            IntakeRoutingStatus.RoutedForReview, "queue-a", null, false);

        // Routed reports are not coordinator-visible at all.
        Assert.Equal(IntakeDecisionStatus.NotFoundOrNotAuthorized, status);
    }

    [Fact]
    public void Assign_OnAwaitingReview_IsInvalid()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.AssignQueue, "queue-b", "Correcting.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.AwaitingReview, 1,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Invalid, status);
        Assert.Null(effect);
    }

    [Fact]
    public void Clarify_SetsFlagWithoutMovingState()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.RequestClarification, null, "Need onset date.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.NotNull(effect);
        Assert.True(effect.SetClarification);
        Assert.Null(effect.NextStatus);
    }

    [Fact]
    public void NoCoverage_MovesNeedsRoutingToUncovered()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.RecordNoServiceCoverage, null, "No catchment covers this area.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.NotNull(effect);
        Assert.Equal(IntakeRoutingStatus.NoServiceCoverage, effect.NextRouting);
        Assert.Null(effect.NextStatus);
    }

    [Fact]
    public void NoCoverage_OnAlreadyUncovered_ReplaysWithoutChange()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 3, IntakeCoordinationAction.RecordNoServiceCoverage, null, "Still uncovered.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 3,
            IntakeRoutingStatus.NoServiceCoverage, null, "coordinator-1", false);

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.Same(IntakeCoordinationEffect.NoChange, effect);
    }

    [Theory]
    [InlineData(IntakeStatus.AcceptedIntoCase)]
    [InlineData(IntakeStatus.LinkedAsEvidence)]
    [InlineData(IntakeStatus.Declined)]
    public void AnyAction_OnTerminalReport_Conflicts(IntakeStatus status)
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 4, IntakeCoordinationAction.RequestClarification, null, "Too late.");

        var (result, effect) = IntakeCoordinationTransition.Evaluate(
            command, status, 4, IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Conflict, result);
        Assert.Null(effect);
    }

    [Fact]
    public void VersionDrift_Conflicts()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.ClaimForReview, null, "Taking review.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 2,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Conflict, status);
        Assert.Null(effect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void MissingReason_IsInvalid(string? reason)
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.ClaimForReview, null, reason);

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1,
            IntakeRoutingStatus.NeedsRouting, null, null, false);

        Assert.Equal(IntakeDecisionStatus.Invalid, status);
        Assert.Null(effect);
    }

    [Fact]
    public void RoutedReport_IsNotCoordinatorVisible()
    {
        var command = new IntakeCoordinationCommand(
            Guid.NewGuid(), 1, IntakeCoordinationAction.RequestClarification, null, "Question.");

        var (status, effect) = IntakeCoordinationTransition.Evaluate(
            command, IntakeStatus.AwaitingReview, 1,
            IntakeRoutingStatus.RoutedForReview, "queue-zw-harare", null, false);

        Assert.Equal(IntakeDecisionStatus.NotFoundOrNotAuthorized, status);
        Assert.Null(effect);
    }
}
