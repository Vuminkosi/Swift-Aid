using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

public sealed class CaseCommandAuthorizationMapTests
{
    [Fact]
    public void Assignment_acknowledgement_maps_only_to_its_dedicated_permission()
    {
        var requirement = CaseCommandAuthorizationMap.Resolve(
            new AcknowledgeAssignmentCommand(Guid.NewGuid(), 3, 2, null, Now), CreateAggregate(CaseAction.Reassign));

        Assert.Equal([CaseOperation.AcknowledgeAssignment], requirement.RequiredOperations);
        Assert.Equal(CaseCapability.AcknowledgeAssignment, requirement.RequiredCapabilities);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CaseId = Guid.Parse("379cd4d8-9bea-47fa-bb6a-b159e9378d70");
    private static readonly Guid RequestId = Guid.Parse("f3fdbb8d-6928-4c9c-beb3-30e96a02ee8c");

    [Fact]
    public void Correct_report_and_action_request_map_to_narrow_server_owned_requirements()
    {
        var aggregate = CreateAggregate(CaseAction.Reassign);
        var correction = CaseCommandAuthorizationMap.Resolve(
            new CorrectReportCommand(Guid.NewGuid(), 3, "corrected source narrative", "source correction", null, Now), aggregate);
        var request = CaseCommandAuthorizationMap.Resolve(
            new RequestCaseActionCommand(Guid.NewGuid(), 3, Guid.NewGuid(), CaseAction.Resolve,
                "request administrative review", [], null, null, Now), aggregate);

        Assert.Equal([CaseOperation.CorrectReport], correction.RequiredOperations);
        Assert.Equal(CaseCapability.CorrectReport, correction.RequiredCapabilities);
        Assert.Equal([CaseOperation.RequestProtectedAction], request.RequiredOperations);
        Assert.Equal(CaseCapability.RequestProtectedAction, request.RequiredCapabilities);
    }

    [Theory]
    [InlineData(CaseAction.Reassign, CaseOperation.AssignCase, CaseCapability.AssignCase)]
    [InlineData(CaseAction.Assist, CaseOperation.AssignCase, CaseCapability.AssignCase)]
    [InlineData(CaseAction.Resolve, CaseOperation.ResolveCase, CaseCapability.ResolveCase)]
    [InlineData(CaseAction.Reopen, CaseOperation.ReopenCase, CaseCapability.ReopenCase)]
    public void Approved_review_requires_review_and_the_authoritative_requested_action(
        CaseAction requestedAction,
        CaseOperation actionOperation,
        CaseCapability actionCapability)
    {
        var aggregate = CreateAggregate(requestedAction);
        var command = new ReviewCaseActionRequestCommand(
            Guid.NewGuid(), 3, RequestId, CaseActionReviewOutcome.Approved, null,
            "review accepted operation", null, Now);

        var requirement = CaseCommandAuthorizationMap.Resolve(command, aggregate);

        Assert.Equal([CaseOperation.ReviewProtectedAction, actionOperation], requirement.RequiredOperations);
        Assert.Equal(CaseCapability.ReviewProtectedAction | actionCapability, requirement.RequiredCapabilities);
    }

    [Theory]
    [InlineData(CaseActionReviewOutcome.Pending)]
    [InlineData(CaseActionReviewOutcome.Denied)]
    public void Pending_or_denied_review_requires_only_review_authority(CaseActionReviewOutcome outcome)
    {
        var aggregate = CreateAggregate(CaseAction.Reassign);
        var command = new ReviewCaseActionRequestCommand(
            Guid.NewGuid(), 3, RequestId, outcome, null, "review decision", null, Now);

        var requirement = CaseCommandAuthorizationMap.Resolve(command, aggregate);

        Assert.Equal([CaseOperation.ReviewProtectedAction], requirement.RequiredOperations);
        Assert.Equal(CaseCapability.ReviewProtectedAction, requirement.RequiredCapabilities);
    }

    [Fact]
    public void Approved_review_uses_only_the_replayed_request_and_missing_request_fails_closed()
    {
        var aggregate = CreateAggregate(CaseAction.Reopen);
        var unrelatedBodyHasNoAction = new ReviewCaseActionRequestCommand(
            Guid.NewGuid(), 3, RequestId, CaseActionReviewOutcome.Approved, "owner-target",
            "review reopened case", null, Now);
        var requirement = CaseCommandAuthorizationMap.Resolve(unrelatedBodyHasNoAction, aggregate);

        Assert.Equal([CaseOperation.ReviewProtectedAction, CaseOperation.ReopenCase], requirement.RequiredOperations);

        var missing = unrelatedBodyHasNoAction with { RequestId = Guid.NewGuid() };
        Assert.Throws<InvalidDataException>(() => CaseCommandAuthorizationMap.Resolve(missing, aggregate));
    }

    [Fact]
    public void Direct_mutation_commands_map_to_their_specific_capabilities()
    {
        var aggregate = CreateAggregate(CaseAction.Resolve);
        var resolve = CaseCommandAuthorizationMap.Resolve(
            new ResolveCaseCommand(Guid.NewGuid(), 3, "resolve with recorded evidence", null, Now), aggregate);
        var reopen = CaseCommandAuthorizationMap.Resolve(
            new ReopenCaseCommand(Guid.NewGuid(), 3, "reopen for follow-up", "owner-target", null, Now), aggregate);

        Assert.Equal([CaseOperation.ResolveCase], resolve.RequiredOperations);
        Assert.Equal(CaseCapability.ResolveCase, resolve.RequiredCapabilities);
        Assert.Equal([CaseOperation.ReopenCase], reopen.RequiredOperations);
        Assert.Equal(CaseCapability.ReopenCase, reopen.RequiredCapabilities);
    }

    [Fact]
    public void Monitoring_commands_map_to_narrow_server_owned_requirements()
    {
        var aggregate = CreateAggregate(CaseAction.Resolve);
        var observation = CaseCommandAuthorizationMap.Resolve(
            new RecordObservationCommand(Guid.NewGuid(), 3, Guid.NewGuid(), "visit note", "home-visit", "record visit", null, Now), aggregate);
        var schedule = CaseCommandAuthorizationMap.Resolve(
            new ScheduleFollowUpCommand(Guid.NewGuid(), 3, Guid.NewGuid(), "revisit holding", "worker-1", Now.AddDays(2), "routine check", null, Now), aggregate);
        var followUpAcknowledgement = CaseCommandAuthorizationMap.Resolve(
            new AcknowledgeFollowUpCommand(Guid.NewGuid(), 3, Guid.NewGuid(), null, Now), aggregate);
        var outcome = CaseCommandAuthorizationMap.Resolve(
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 3, Guid.NewGuid(), FollowUpOutcome.Missed, "nobody present", null, "attempt logged", null, Now), aggregate);
        var raise = CaseCommandAuthorizationMap.Resolve(
            new RaiseEscalationCommand(Guid.NewGuid(), 3, Guid.NewGuid(), "holding unreachable twice", [], "duty-officer", null, Now), aggregate);
        var acknowledge = CaseCommandAuthorizationMap.Resolve(
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 3, Guid.NewGuid(), null, "acknowledged", null, Now), aggregate);
        var close = CaseCommandAuthorizationMap.Resolve(
            new CloseEscalationCommand(Guid.NewGuid(), 3, Guid.NewGuid(), "supervisor visited", "resolved", null, Now), aggregate);

        Assert.Equal([CaseOperation.RecordObservation], observation.RequiredOperations);
        Assert.Equal(CaseCapability.RecordObservation, observation.RequiredCapabilities);
        Assert.Equal([CaseOperation.ManageFollowUp], schedule.RequiredOperations);
        Assert.Equal(CaseCapability.ManageFollowUp, schedule.RequiredCapabilities);
        Assert.Equal([CaseOperation.ManageFollowUp], followUpAcknowledgement.RequiredOperations);
        Assert.Equal(CaseCapability.ManageFollowUp, followUpAcknowledgement.RequiredCapabilities);
        Assert.Equal([CaseOperation.ManageFollowUp], outcome.RequiredOperations);
        Assert.Equal(CaseCapability.ManageFollowUp, outcome.RequiredCapabilities);
        Assert.Equal([CaseOperation.ManageEscalation], raise.RequiredOperations);
        Assert.Equal(CaseCapability.ManageEscalation, raise.RequiredCapabilities);
        Assert.Equal([CaseOperation.ManageEscalation], acknowledge.RequiredOperations);
        Assert.Equal(CaseCapability.ManageEscalation, acknowledge.RequiredCapabilities);
        Assert.Equal([CaseOperation.ManageEscalation], close.RequiredOperations);
        Assert.Equal(CaseCapability.ManageEscalation, close.RequiredCapabilities);
    }

    [Fact]
    public void Creation_and_malformed_authoritative_requests_are_not_authorized()
    {
        var aggregate = CreateAggregate((CaseAction)999);
        var report = new ReportFacts(CaseKind.Human, Sector.HumanHealth, "synthetic source report", [new ReportedObservation("synthetic observation")]);
        var create = new AcceptCaseCommand(Guid.NewGuid(), CaseId, report, "owner", "workspace", null, Now);
        var invalidRequest = new RequestCaseActionCommand(
            Guid.NewGuid(), 3, Guid.NewGuid(), (CaseAction)999, "request with unknown action", [], null, null, Now);
        var review = new ReviewCaseActionRequestCommand(
            Guid.NewGuid(), 3, RequestId, CaseActionReviewOutcome.Approved, null,
            "review invalid request", null, Now);

        Assert.Throws<NotSupportedException>(() => CaseCommandAuthorizationMap.Resolve(create, aggregate));
        Assert.Throws<InvalidDataException>(() => CaseCommandAuthorizationMap.Resolve(invalidRequest, aggregate));
        Assert.Throws<InvalidDataException>(() => CaseCommandAuthorizationMap.Resolve(review, aggregate));
    }

    private static CaseAggregate CreateAggregate(CaseAction action)
    {
        var actorId = "synthetic-reviewer";
        var acceptanceCommandId = Guid.Parse("de71c8ef-83a8-4d21-8f78-00909c018c41");
        var requestCommandId = Guid.Parse("a417fa8d-2119-4c1e-8fc0-a8bbcc80c99a");
        var report = new ReportFacts(
            CaseKind.Human,
            Sector.HumanHealth,
            "synthetic source report",
            [new ReportedObservation("synthetic observation")]);
        CaseEvent[] events =
        [
            new CaseReported(CaseId, 1, 1, acceptanceCommandId, actorId, null, Now, report, "workspace-a"),
            new CaseAssigned(CaseId, 2, 1, acceptanceCommandId, actorId, null, Now, actorId),
            new CaseActionRequested(CaseId, 3, 1, requestCommandId, actorId, null, Now, RequestId,
                action, "synthetic request reason", [], action is CaseAction.Reassign or CaseAction.Assist ? "owner-target" : null)
        ];

        return CaseAggregate.Replay(events);
    }
}
