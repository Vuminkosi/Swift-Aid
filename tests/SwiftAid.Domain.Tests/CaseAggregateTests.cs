using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using System.Text.Json;

namespace SwiftAid.Domain.Tests;

public sealed class CaseAggregateTests
{
    [Fact]
    public void InitialAcceptance_EmitsReportAndAssignmentAndStartsVersionsAtTwoAndOne()
    {
        var actor = new ActorCapabilities("officer-1", CaseCapability.AcceptReport);
        var command = new AcceptCaseCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ReportFacts(CaseKind.Human, Sector.HumanHealth, "reported narrative", []),
            "officer-1",
            "workspace-1",
            DateTimeOffset.Parse("2026-10-26T08:00:00Z"),
            DateTimeOffset.Parse("2026-10-26T08:01:00Z"));

        var events = CaseAggregate.Create(command, actor);
        var aggregate = CaseAggregate.Replay(events);

        Assert.Collection(events,
            item => Assert.IsType<CaseReported>(item),
            item => Assert.IsType<CaseAssigned>(item));
        Assert.Equal(2, aggregate.AggregateVersion);
        Assert.Equal(1, aggregate.ReportRevision);
        Assert.Equal(1, aggregate.WorkflowVersion);
        Assert.Equal(0, aggregate.ClassificationVersion);
        Assert.Equal(0, aggregate.LaboratoryVersion);
        Assert.Equal(0, aggregate.OutcomeVersion);
        Assert.Equal(LaboratoryState.NotRecorded, aggregate.Laboratory);
        Assert.Equal(ReportedValueState.Unknown, aggregate.Outcome.State);
        Assert.Equal(1, aggregate.WorkflowVersion);
        Assert.Equal(0, aggregate.ClassificationVersion);
        Assert.Equal(0, aggregate.LaboratoryVersion);
        Assert.Equal(0, aggregate.OutcomeVersion);
        Assert.Equal(LaboratoryState.NotRecorded, aggregate.Laboratory);
        Assert.Equal(ReportedValueState.Unknown, aggregate.Outcome.State);
        Assert.Equal(CaseWorkflow.Reported, aggregate.Workflow);
        Assert.Equal(ClinicalClassification.Unclassified, aggregate.Classification);
        Assert.Equal("officer-1", aggregate.AccountableOwnerId);
        Assert.Equal(events[0].EventId, CaseAggregate.Replay(events).Events[0].EventId);
        Assert.NotEqual(events[0].EventId, events[1].EventId);
    }

    [Fact]
    public void Create_RequiresAcceptReportAndOnlyAllowsTheReportingActorAsInitialOwner()
    {
        var commandId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var report = new ReportFacts(CaseKind.Human, Sector.HumanHealth, "reported narrative", []);
        var command = new AcceptCaseCommand(commandId, caseId, report, "officer-1", "workspace-1", null, DateTimeOffset.UtcNow);

        var events = CaseAggregate.Create(command, new ActorCapabilities("officer-1", CaseCapability.AcceptReport));
        var aggregate = CaseAggregate.Replay(events);

        Assert.Collection(events,
            item => Assert.IsType<CaseReported>(item),
            item => Assert.IsType<CaseAssigned>(item));
        Assert.Equal(2, aggregate.AggregateVersion);
        Assert.Equal(1, aggregate.ReportRevision);
        Assert.Equal("officer-1", aggregate.AccountableOwnerId);
        Assert.Equal("officer-1", Assert.IsType<CaseAssigned>(events[1]).AccountableOwnerId);

        Assert.Throws<UnauthorizedAccessException>(() => CaseAggregate.Create(
            command,
            new ActorCapabilities("officer-1", CaseCapability.AssignCase)));
        Assert.Throws<ArgumentException>(() => CaseAggregate.Create(
            command with { AccountableOwnerId = "worker-1" },
            new ActorCapabilities("officer-1", CaseCapability.AcceptReport)));
    }

    [Fact]
    public void UnknownOccurrenceTime_RemainsUnknownAndIsNotReplacedByServerRecordedTime()
    {
        var command = new AcceptCaseCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ReportFacts(CaseKind.Human, Sector.HumanHealth, "reported narrative", []),
            "officer-1",
            "workspace-1",
            null,
            DateTimeOffset.Parse("2026-10-26T08:01:00Z"));

        var events = CaseAggregate.Create(command, new ActorCapabilities("officer-1", CaseCapability.AcceptReport));

        Assert.Null(events[0].OccurredAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T08:01:00Z"), events[0].RecordedAtUtc);
    }

    [Fact]
    public void Replay_IsDeterministic_AndRejectsOutOfOrderEvents()
    {
        var events = CaseAggregate.Create(
            new AcceptCaseCommand(Guid.NewGuid(), Guid.NewGuid(), new ReportFacts(CaseKind.Animal, Sector.AnimalHealth, "animal concern", []), "officer-1", "workspace-1", null, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.AcceptReport));

        var first = CaseAggregate.Replay(events);
        var second = CaseAggregate.Replay(events);

        Assert.Equal(first.Events, second.Events);
        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay(events.Reverse()));

        var reportOnly = Assert.IsType<CaseReported>(events[0]);
        var skippedAssignment = new CaseActionRequested(
            reportOnly.CaseId,
            2,
            1,
            Guid.NewGuid(),
            "officer-1",
            null,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            CaseAction.Reopen,
            "request reopening",
            [],
            null);
        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay([reportOnly, skippedAssignment]));
    }

    [Fact]
    public void Replay_RejectsInitialAssignmentToDifferentActor()
    {
        var caseId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var reportedAt = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var report = new CaseReported(
            caseId,
            1,
            1,
            commandId,
            "officer-1",
            null,
            reportedAt,
            new ReportFacts(CaseKind.Human, Sector.HumanHealth, "reported narrative", []),
            "workspace-1");
        var assignment = new CaseAssigned(
            caseId,
            2,
            1,
            commandId,
            "officer-1",
            null,
            reportedAt,
            "worker-1");

        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay([report, assignment]));
    }

    [Fact]
    public void PresenceStates_DistinguishUnknownNotRecordedNotApplicableAbsentAndZero()
    {
        var values = new[]
        {
            ReportedValue<int>.Unknown,
            ReportedValue<int>.NotRecorded,
            ReportedValue<int>.NotApplicable,
            ReportedValue<int>.Absent,
            ReportedValue<int>.Known(0)
        };

        Assert.Equal(5, values.Distinct().Count());
        Assert.All(values.Take(4), value =>
        {
            Assert.False(value.HasValue);
            Assert.Throws<InvalidOperationException>(() => value.Value);
        });
        Assert.True(values[4].HasValue);
        Assert.Equal(0, values[4].Value);
    }

    [Fact]
    public void Correction_AddsARevisionWithoutMutatingTheOriginalReportOrReopeningResolvedCase()
    {
        var aggregate = AcceptedResolvedCase();
        var originalReport = aggregate.Report;
        var command = new CorrectReportCommand(Guid.NewGuid(), aggregate.AggregateVersion, "Corrected observation", "reporter clarification", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var events = aggregate.Decide(command, new ActorCapabilities("officer-1", CaseCapability.CorrectReport));
        var corrected = CaseAggregate.Replay(aggregate.Events.Concat(events));

        Assert.Equal(2, corrected.ReportRevision);
        Assert.Equal(originalReport, aggregate.Report);
        Assert.Equal(CaseWorkflow.Resolved, corrected.Workflow);
        Assert.Equal(ClinicalClassification.Unclassified, corrected.Classification);
    }

    [Fact]
    public void StaleCorrectionAndUnauthorizedResolution_AreRejectedWithoutChangingState()
    {
        var aggregate = AcceptedCase();

        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(
            new CorrectReportCommand(Guid.NewGuid(), aggregate.AggregateVersion - 1, "Corrected observation", "reporter clarification", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.CorrectReport)));
        Assert.Throws<UnauthorizedAccessException>(() => aggregate.Decide(
            new ResolveCaseCommand(Guid.NewGuid(), aggregate.AggregateVersion, "work complete", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction)));

        Assert.Equal(2, aggregate.AggregateVersion);
        Assert.Equal(CaseWorkflow.Reported, aggregate.Workflow);
    }

    [Fact]
    public void ProtectedActionRequest_DoesNotPerformTheRequestedAction()
    {
        var aggregate = AcceptedCase();
        var command = new RequestCaseActionCommand(Guid.NewGuid(), aggregate.AggregateVersion, Guid.NewGuid(), CaseAction.Reassign, "different area requested", [], "new-worker-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var events = aggregate.Decide(command, new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction));
        var requested = CaseAggregate.Replay(aggregate.Events.Concat(events));

        Assert.Single(events);
        Assert.IsType<CaseActionRequested>(events[0]);
        Assert.Equal("officer-1", requested.AccountableOwnerId);
        Assert.Equal(CaseWorkflow.Reported, requested.Workflow);
        Assert.Equal(3, requested.AggregateVersion);
    }

    [Fact]
    public void ReopenRequest_LeavesCaseResolvedUntilAuthorizedReopenCommand()
    {
        var aggregate = AcceptedResolvedCase();
        var request = aggregate.Decide(
            new RequestCaseActionCommand(Guid.NewGuid(), aggregate.AggregateVersion, Guid.NewGuid(), CaseAction.Reopen, "new operational work", [], null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction));
        var requested = CaseAggregate.Replay(aggregate.Events.Concat(request));

        Assert.Equal(CaseWorkflow.Resolved, requested.Workflow);

        Assert.Throws<UnauthorizedAccessException>(() => requested.Decide(
            new ReopenCaseCommand(Guid.NewGuid(), requested.AggregateVersion, "new operational work", "worker-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction)));
    }

    [Fact]
    public void Resolution_DoesNotChangeClinicalClassification_AndReopeningRequiresReason()
    {
        var aggregate = AcceptedCase();
        var resolvedEvents = aggregate.Decide(
            new ResolveCaseCommand(Guid.NewGuid(), aggregate.AggregateVersion, "administrative work complete", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ResolveCase));
        var resolved = CaseAggregate.Replay(aggregate.Events.Concat(resolvedEvents));

        Assert.Equal(CaseWorkflow.Resolved, resolved.Workflow);
        Assert.Equal(resolved.AggregateVersion, resolved.WorkflowVersion);
        Assert.Equal(0, resolved.ClassificationVersion);
        Assert.Equal(0, resolved.LaboratoryVersion);
        Assert.Equal(0, resolved.OutcomeVersion);
        Assert.Equal(resolved.AggregateVersion, resolved.WorkflowVersion);
        Assert.Equal(0, resolved.ClassificationVersion);
        Assert.Equal(0, resolved.LaboratoryVersion);
        Assert.Equal(0, resolved.OutcomeVersion);
        Assert.Equal(ClinicalClassification.Unclassified, resolved.Classification);
        Assert.Throws<ArgumentException>(() => resolved.Decide(
            new ReopenCaseCommand(Guid.NewGuid(), resolved.AggregateVersion, " ", "worker-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ReopenCase)));
    }

    [Fact]
    public void Reopen_RequiresCapabilityAndPreservesClosureHistory()
    {
        var resolved = AcceptedResolvedCase();
        var beforeReopen = resolved.Events.ToArray();
        var events = resolved.Decide(
            new ReopenCaseCommand(Guid.NewGuid(), resolved.AggregateVersion, "new support work", "worker-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ReopenCase));
        var reopened = CaseAggregate.Replay(resolved.Events.Concat(events));

        Assert.Equal(CaseWorkflow.Active, reopened.Workflow);
        Assert.Equal(beforeReopen, reopened.Events.Take(beforeReopen.Length));
        Assert.IsType<CaseReopened>(events.Single());
    }

    [Fact]
    public void ReassignmentRequest_ChangesOwnerOnlyWhenAuthorizedReviewExecutesIt()
    {
        var aggregate = AcceptedCase();
        var request = aggregate.Decide(
            new RequestCaseActionCommand(Guid.NewGuid(), aggregate.AggregateVersion, Guid.NewGuid(), CaseAction.Reassign, "request reassignment", [], "worker-2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction));
        var requested = CaseAggregate.Replay(aggregate.Events.Concat(request));
        var requestId = Assert.IsType<CaseActionRequested>(request.Single()).RequestId;

        Assert.Equal("officer-1", requested.AccountableOwnerId);

        var review = requested.Decide(
            new ReviewCaseActionRequestCommand(Guid.NewGuid(), requested.AggregateVersion, requestId, CaseActionReviewOutcome.Approved, null, "authorized transfer", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ReviewProtectedAction | CaseCapability.AssignCase));
        var reassigned = CaseAggregate.Replay(requested.Events.Concat(review));
        var decision = Assert.IsType<CaseActionRequestReviewed>(review.Last());

        Assert.Equal("worker-2", reassigned.AccountableOwnerId);
        Assert.Equal(CaseActionReviewOutcome.Approved, decision.Outcome);
        Assert.Equal(Assert.IsType<CaseAssigned>(review[0]).EventId, decision.LinkedAcceptedEventId);

        var streamingAggregate = CaseAggregate.Replay(aggregate.Events.Concat(request));
        streamingAggregate.Apply(review[0]);
        Assert.Equal("officer-1", streamingAggregate.AccountableOwnerId);
        Assert.Equal(requested.AggregateVersion, streamingAggregate.AggregateVersion);
        streamingAggregate.Apply(review[1]);
        Assert.Equal("worker-2", streamingAggregate.AccountableOwnerId);

        var wireEvent = new
        {
            schemaVersion = 1,
            eventId = decision.EventId,
            eventType = "CaseActionRequestReviewed",
            caseId = decision.CaseId,
            aggregateVersion = decision.AggregateVersion,
            reportRevision = decision.ReportRevision,
            commandId = decision.CommandId,
            actorId = decision.ActorId,
            occurredAtUtc = decision.OccurredAtUtc,
            recordedAtUtc = decision.RecordedAtUtc,
            payload = new
            {
                requestId = decision.RequestId,
                outcome = decision.Outcome.ToString(),
                linkedAcceptedEventId = decision.LinkedAcceptedEventId,
                reason = decision.Reason
            }
        };
        using var serializedEvent = JsonDocument.Parse(JsonSerializer.Serialize(wireEvent));
        var payload = serializedEvent.RootElement.GetProperty("payload");
        Assert.Equal(requestId, payload.GetProperty("requestId").GetGuid());
        Assert.Equal(Assert.IsType<CaseAssigned>(review[0]).EventId, payload.GetProperty("linkedAcceptedEventId").GetGuid());

        var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/events/case-event.v1.schema.json"));
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var reviewSchema = schemaDocument.RootElement.GetProperty("$defs").GetProperty("actionReview");
        Assert.Contains("linkedAcceptedEventId", reviewSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var conditions = reviewSchema.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.Contains(conditions, condition =>
            condition.GetProperty("if").GetProperty("properties").GetProperty("outcome").TryGetProperty("const", out var outcome) &&
            outcome.GetString() == "Approved" &&
            condition.GetProperty("then").GetProperty("properties").GetProperty("linkedAcceptedEventId").GetProperty("type").GetString() == "string");
        Assert.Contains(conditions, condition =>
            condition.GetProperty("if").GetProperty("properties").GetProperty("outcome").TryGetProperty("enum", out var outcomes) &&
            outcomes.EnumerateArray().Any(value => value.GetString() == "Denied") &&
            condition.GetProperty("then").GetProperty("properties").GetProperty("linkedAcceptedEventId").GetProperty("type").GetString() == "null");
    }

    [Fact]
    public void ReassignmentStillRequiresAssignCapabilityAndNonEmptyReviewReason()
    {
        var aggregate = AcceptedCase();
        var requestEvents = aggregate.Decide(
            new RequestCaseActionCommand(Guid.NewGuid(), aggregate.AggregateVersion, Guid.NewGuid(), CaseAction.Reassign, "request reassignment", [], "worker-2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.RequestProtectedAction));
        var requested = CaseAggregate.Replay(aggregate.Events.Concat(requestEvents));
        var requestId = Assert.IsType<CaseActionRequested>(requestEvents.Single()).RequestId;

        Assert.Throws<UnauthorizedAccessException>(() => requested.Decide(
            new ReviewCaseActionRequestCommand(Guid.NewGuid(), requested.AggregateVersion, requestId, CaseActionReviewOutcome.Approved, null, "approved transfer", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ReviewProtectedAction)));
        Assert.Throws<ArgumentException>(() => requested.Decide(
            new ReviewCaseActionRequestCommand(Guid.NewGuid(), requested.AggregateVersion, requestId, CaseActionReviewOutcome.Approved, null, " ", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ReviewProtectedAction | CaseCapability.AssignCase)));
        Assert.Equal("officer-1", requested.AccountableOwnerId);
        Assert.Equal(3, requested.AggregateVersion);
    }

    [Fact]
    public void Replay_RejectsUnpairedReassignmentAndReassignmentAfterResolution()
    {
        var accepted = AcceptedCase();
        var unpairedAssignment = new CaseAssigned(
            accepted.CaseId,
            accepted.AggregateVersion + 1,
            accepted.ReportRevision,
            Guid.NewGuid(),
            "officer-1",
            null,
            DateTimeOffset.UtcNow,
            "worker-2");

        var streaming = CaseAggregate.Replay(accepted.Events);
        var streamingRequest = new CaseActionRequested(
            accepted.CaseId,
            accepted.AggregateVersion + 1,
            accepted.ReportRevision,
            Guid.NewGuid(),
            "worker-1",
            null,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            CaseAction.Reassign,
            "reassignment requested",
            [],
            "worker-2");
        streaming.Apply(streamingRequest);
        var ownerBeforeUnpairedApply = streaming.AccountableOwnerId;
        var versionBeforeUnpairedApply = streaming.AggregateVersion;
        var pendingAssignment = unpairedAssignment with
        {
            AggregateVersion = streaming.AggregateVersion + 1,
            CommandId = streamingRequest.CommandId
        };
        streaming.Apply(pendingAssignment);
        Assert.Equal(ownerBeforeUnpairedApply, streaming.AccountableOwnerId);
        Assert.Equal(versionBeforeUnpairedApply, streaming.AggregateVersion);
        Assert.Throws<InvalidOperationException>(() => streaming.Decide(
            new ResolveCaseCommand(Guid.NewGuid(), streaming.AggregateVersion, "complete work", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ResolveCase)));
        Assert.Throws<InvalidDataException>(() => streaming.Apply(new CaseResolved(
            accepted.CaseId,
            streaming.AggregateVersion + 2,
            accepted.ReportRevision,
            Guid.NewGuid(),
            "officer-1",
            null,
            DateTimeOffset.UtcNow,
            "unrelated transition")));
        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay(accepted.Events.Append(unpairedAssignment)));

        var resolved = AcceptedResolvedCase();
        var assignmentAfterResolution = unpairedAssignment with
        {
            CaseId = resolved.CaseId,
            AggregateVersion = resolved.AggregateVersion + 1,
            ReportRevision = resolved.ReportRevision
        };

        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay(resolved.Events.Append(assignmentAfterResolution)));
    }

    [Fact]
    public void Apply_SnapshotsCallerOwnedEventCollections()
    {
        var aggregate = AcceptedCase();
        var evidence = new List<string> { "evidence-1" };
        var request = new CaseActionRequested(
            aggregate.CaseId,
            aggregate.AggregateVersion + 1,
            aggregate.ReportRevision,
            Guid.NewGuid(),
            "worker-1",
            null,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            CaseAction.Reassign,
            "request reassignment",
            evidence,
            "worker-2");

        aggregate.Apply(request);
        evidence.Add("injected-after-apply");

        var storedRequest = Assert.Single(aggregate.Events.OfType<CaseActionRequested>());
        Assert.Equal(["evidence-1"], storedRequest.EvidenceEventIds);

        var observations = new List<ReportedObservation> { new("reported correction") };
        var correctedReport = aggregate.Report! with
        {
            Narrative = "corrected narrative",
            Observations = observations
        };
        var correction = new CaseReportCorrected(
            aggregate.CaseId,
            aggregate.AggregateVersion + 1,
            aggregate.ReportRevision + 1,
            Guid.NewGuid(),
            "officer-1",
            null,
            DateTimeOffset.UtcNow,
            correctedReport,
            "source correction",
            aggregate.ReportRevision);

        aggregate.Apply(correction);
        observations.Add(new ReportedObservation("injected after apply"));

        Assert.Single(aggregate.Report!.Observations);
        Assert.Single(Assert.Single(aggregate.Events.OfType<CaseReportCorrected>()).CorrectedReport.Observations);
    }

    [Fact]
    public void InvalidApprovedResolutionRequest_EmitsNoApprovalEvent()
    {
        var aggregate = AcceptedResolvedCase();
        var request = aggregate.Decide(
            new RequestCaseActionCommand(Guid.NewGuid(), aggregate.AggregateVersion, Guid.NewGuid(), CaseAction.Resolve, "resolve request", [], null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction));
        var requested = CaseAggregate.Replay(aggregate.Events.Concat(request));
        var requestId = Assert.IsType<CaseActionRequested>(request.Single()).RequestId;

        Assert.Throws<InvalidOperationException>(() => requested.Decide(
            new ReviewCaseActionRequestCommand(Guid.NewGuid(), requested.AggregateVersion, requestId, CaseActionReviewOutcome.Approved, null, "attempt resolution", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ReviewProtectedAction | CaseCapability.ResolveCase)));
        Assert.DoesNotContain(requested.Events, item => item is CaseActionRequestReviewed);
    }

    [Fact]
    public void DeniedReview_SerializesWithoutAnAcceptedEventLink()
    {
        var aggregate = AcceptedCase();
        var requestEvents = aggregate.Decide(
            new RequestCaseActionCommand(Guid.NewGuid(), aggregate.AggregateVersion, Guid.NewGuid(), CaseAction.Reassign, "request reassignment", [], "worker-2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction));
        var requested = CaseAggregate.Replay(aggregate.Events.Concat(requestEvents));
        var requestId = Assert.IsType<CaseActionRequested>(requestEvents.Single()).RequestId;
        var review = Assert.IsType<CaseActionRequestReviewed>(requested.Decide(
            new ReviewCaseActionRequestCommand(Guid.NewGuid(), requested.AggregateVersion, requestId, CaseActionReviewOutcome.Denied, null, "request declined", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ReviewProtectedAction)).Single());

        using var serializedEvent = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            eventType = "CaseActionRequestReviewed",
            payload = new
            {
                requestId = review.RequestId,
                outcome = review.Outcome.ToString(),
                linkedAcceptedEventId = review.LinkedAcceptedEventId,
                reason = review.Reason
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var payload = serializedEvent.RootElement.GetProperty("payload");
        Assert.Equal(requestId, payload.GetProperty("requestId").GetGuid());
        Assert.Equal("Denied", payload.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("linkedAcceptedEventId").ValueKind);
    }

    private static CaseAggregate AcceptedCase() => CaseAggregate.Replay(CaseAggregate.Create(
        new AcceptCaseCommand(Guid.NewGuid(), Guid.NewGuid(), new ReportFacts(CaseKind.Human, Sector.HumanHealth, "reported narrative", []), "officer-1", "workspace-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new ActorCapabilities("officer-1", CaseCapability.AcceptReport)));

    private static CaseAggregate AcceptedResolvedCase()
    {
        var accepted = AcceptedCase();
        var resolution = accepted.Decide(
            new ResolveCaseCommand(Guid.NewGuid(), accepted.AggregateVersion, "administrative work complete", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ActorCapabilities("officer-1", CaseCapability.ResolveCase));
        return CaseAggregate.Replay(accepted.Events.Concat(resolution));
    }
}
