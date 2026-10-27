using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Tests;

public sealed class IntakeDecisionTransitionTests
{
    [Fact]
    public void RouteForReview_OnUnverifiedRoutedReport_AppliesAwaitingReview()
    {
        var command = new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.RouteForReview, null);

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare");

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.Equal(IntakeStatus.AwaitingReview, next);
    }

    [Fact]
    public void RouteForReview_OnAwaitingReview_ReappliesIdempotently()
    {
        var command = new IntakeDecision(Guid.NewGuid(), 3, IntakeDecisionKind.RouteForReview, null);

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, IntakeStatus.AwaitingReview, 3, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare");

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.Equal(IntakeStatus.AwaitingReview, next);
    }

    [Theory]
    [InlineData(IntakeRoutingStatus.NeedsRouting)]
    [InlineData(IntakeRoutingStatus.NoServiceCoverage)]
    public void RouteForReview_WithoutRoutedCoverage_IsInvalid(IntakeRoutingStatus routing)
    {
        var command = new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.RouteForReview, null);

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1, routing, null);

        Assert.Equal(IntakeDecisionStatus.Invalid, status);
        Assert.Null(next);
    }

    [Fact]
    public void RouteForReview_WithRoutedStatusButNoQueue_IsInvalid()
    {
        var command = new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.RouteForReview, null);

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1, IntakeRoutingStatus.RoutedForReview, "  ");

        Assert.Equal(IntakeDecisionStatus.Invalid, status);
        Assert.Null(next);
    }

    [Theory]
    [InlineData(IntakeStatus.ReceivedUnverified)]
    [InlineData(IntakeStatus.AwaitingReview)]
    public void Decline_OnUndecidedReport_AppliesDeclined(IntakeStatus current)
    {
        var command = new IntakeDecision(Guid.NewGuid(), 2, IntakeDecisionKind.Decline, "Duplicate of an earlier report.");

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, current, 2, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare");

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.Equal(IntakeStatus.Declined, next);
    }

    [Theory]
    [InlineData(IntakeStatus.ReceivedUnverified)]
    [InlineData(IntakeStatus.AwaitingReview)]
    public void LinkAsEvidence_OnUndecidedReport_AppliesLinkedStatus(IntakeStatus current)
    {
        var command = new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.LinkAsEvidence, null);

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, current, 1, IntakeRoutingStatus.NoServiceCoverage, null);

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.Equal(IntakeStatus.LinkedAsEvidence, next);
    }

    [Theory]
    [InlineData(IntakeDecisionKind.RouteForReview)]
    [InlineData(IntakeDecisionKind.AcceptIntoCase)]
    [InlineData(IntakeDecisionKind.LinkAsEvidence)]
    [InlineData(IntakeDecisionKind.Decline)]
    public void StaleExpectedVersion_ConflictsBeforeAnyStateChange(IntakeDecisionKind kind)
    {
        // AcceptIntoCase throws only after the version check, so a stale version still conflicts.
        var command = new IntakeDecision(Guid.NewGuid(), 1, kind, null);

        if (kind == IntakeDecisionKind.AcceptIntoCase)
        {
            var (status, next) = IntakeDecisionTransition.Evaluate(
                command, IntakeStatus.ReceivedUnverified, 2, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare");
            Assert.Equal(IntakeDecisionStatus.Conflict, status);
            Assert.Null(next);
            return;
        }

        var (applied, _) = IntakeDecisionTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 2, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare");
        Assert.Equal(IntakeDecisionStatus.Conflict, applied);
    }

    [Fact]
    public void SameDecisionReplay_OnTerminalReport_ReappliesWithoutConflict()
    {
        var command = new IntakeDecision(Guid.NewGuid(), 4, IntakeDecisionKind.Decline, null);

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, IntakeStatus.Declined, 4, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare");

        Assert.Equal(IntakeDecisionStatus.Applied, status);
        Assert.Equal(IntakeStatus.Declined, next);
    }

    [Fact]
    public void DifferentDecision_OnTerminalReport_Conflicts()
    {
        var command = new IntakeDecision(Guid.NewGuid(), 4, IntakeDecisionKind.RouteForReview, null);

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, IntakeStatus.Declined, 4, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare");

        Assert.Equal(IntakeDecisionStatus.Conflict, status);
        Assert.Null(next);
    }

    [Fact]
    public void AcceptIntoCase_ThrowsPromotionUnavailable()
    {
        var command = new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.AcceptIntoCase, null);

        var exception = Assert.Throws<IntakePromotionUnavailableException>(() =>
            IntakeDecisionTransition.Evaluate(
                command, IntakeStatus.AwaitingReview, 1, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare"));

        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void InvalidInputs_AreRejectedWithoutStateChange()
    {
        var command = new IntakeDecision(Guid.Empty, 0, (IntakeDecisionKind)99, new string('x', 5));

        var (status, next) = IntakeDecisionTransition.Evaluate(
            command, IntakeStatus.ReceivedUnverified, 1, IntakeRoutingStatus.RoutedForReview, "queue-zw-harare");

        Assert.Equal(IntakeDecisionStatus.Invalid, status);
        Assert.Null(next);
    }
}
