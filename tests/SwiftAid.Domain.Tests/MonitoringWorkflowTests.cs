using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Domain.Tests;

/// <summary>P5.2 monitoring workflow: observations, follow-ups, and staff-owned escalations (SA-MON, SA-FUP, SA-ESC; AT-18, AT-19).</summary>
public sealed class MonitoringWorkflowTests
{
    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.Parse("2026-10-28T08:00:00Z");
    private static readonly DateTimeOffset DueAt = DateTimeOffset.Parse("2026-10-30T08:00:00Z");
    private static readonly DateTimeOffset RescheduledDueAt = DateTimeOffset.Parse("2026-11-02T08:00:00Z");

    private static (CaseAggregate Aggregate, List<CaseEvent> Events) AcceptedCase()
    {
        var commandId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var report = new ReportFacts(CaseKind.Human, Sector.HumanHealth, "reported narrative", []);
        var command = new AcceptCaseCommand(commandId, caseId, report, "officer-1", "workspace-1", null, RecordedAt);
        var events = CaseAggregate.Create(command, new ActorCapabilities("officer-1", CaseCapability.AcceptReport)).ToList();
        return (CaseAggregate.Replay(events), events);
    }

    private static CaseAggregate Apply(CaseAggregate aggregate, List<CaseEvent> events, CaseCommand command, ActorCapabilities actor)
    {
        var decided = aggregate.Decide(command with { ExpectedVersion = aggregate.AggregateVersion }, actor);
        events.AddRange(decided);
        return CaseAggregate.Replay(events);
    }

    private static ActorCapabilities MonitoringActor() => new("officer-1",
        CaseCapability.RecordObservation | CaseCapability.ManageFollowUp | CaseCapability.ManageEscalation);

    [Fact]
    public void RecordObservation_AppendsNarrativeAndTracksLatest()
    {
        var (aggregate, events) = AcceptedCase();
        var observationId = Guid.NewGuid();
        var command = new RecordObservationCommand(Guid.NewGuid(), 0, observationId, "subject resting, breathing normal", "worker-visit", "visit note", null, RecordedAt);

        aggregate = Apply(aggregate, events, command, MonitoringActor());

        Assert.Equal(1, aggregate.ObservationCount);
        Assert.NotNull(aggregate.LatestObservation);
        Assert.Equal(observationId, aggregate.LatestObservation.ObservationId);
        Assert.Equal("subject resting, breathing normal", aggregate.LatestObservation.Narrative);
        Assert.Equal(CaseWorkflow.Reported, aggregate.Workflow);
    }

    [Fact]
    public void RecordObservation_RejectsDuplicatesAndRequiresCapability()
    {
        var (aggregate, events) = AcceptedCase();
        var observationId = Guid.NewGuid();
        var command = new RecordObservationCommand(Guid.NewGuid(), 0, observationId, "note", "visit", "reason", null, RecordedAt);
        aggregate = Apply(aggregate, events, command, MonitoringActor());

        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new RecordObservationCommand(Guid.NewGuid(), 0, observationId, "note", "visit", "reason", null, RecordedAt),
            MonitoringActor()));
        Assert.Throws<UnauthorizedAccessException>(() => Apply(aggregate, events, command,
            new ActorCapabilities("officer-1", CaseCapability.ManageFollowUp)));
    }

    [Fact]
    public void ScheduleFollowUp_PreservesActorChosenDueTimeVerbatim()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        var command = new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "revisit holding", "worker-1", DueAt, "routine check", null, RecordedAt);

        aggregate = Apply(aggregate, events, command, MonitoringActor());

        var followUp = Assert.Single(aggregate.FollowUps);
        Assert.Equal(followUpId, followUp.FollowUpId);
        Assert.Equal(DueAt, followUp.DueAtUtc);
        Assert.Equal(FollowUpStatus.Scheduled, followUp.Status);
        Assert.Equal("worker-1", followUp.AssigneeActorId);
    }

    [Fact]
    public void ChangeActionOwner_TransfersOpenFollowUpExplicitlyAndReplayPreservesTransfer()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "callback", "worker-1", DueAt, "assign", null, RecordedAt),
            MonitoringActor());

        aggregate = Apply(aggregate, events,
            new ChangeActionOwnerCommand(Guid.NewGuid(), 0, ActionOwnerKind.FollowUp, followUpId, "worker-2", "coverage handoff", null, RecordedAt),
            MonitoringActor());

        var changed = Assert.IsType<ActionOwnerChanged>(events[^1]);
        Assert.Equal("worker-1", changed.PreviousOwnerId);
        Assert.Equal("worker-2", changed.NewOwnerId);
        Assert.Equal("worker-2", Assert.Single(aggregate.FollowUps).AssigneeActorId);
        Assert.Equal(aggregate.FollowUps, CaseAggregate.Replay(events).FollowUps);
    }

    [Fact]
    public void ChangeActionOwner_RejectsWrongPreviousOwnerDuringReplay()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "callback", "worker-1", DueAt, "assign", null, RecordedAt),
            MonitoringActor());

        var invalid = new ActionOwnerChanged(Guid.NewGuid(), aggregate.AggregateVersion + 1, aggregate.ReportRevision,
            Guid.NewGuid(), "officer-1", null, RecordedAt, ActionOwnerKind.FollowUp, followUpId,
            "worker-stale", "worker-2", "handoff");
        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay([.. events, invalid]));
    }

    [Fact]
    public void FollowUpAcknowledgement_IsRecordedOnceByAssignedWorkerAndPreservesOpenWork()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "record next reported observation", "worker-1", DueAt, "staff scheduled", null, RecordedAt),
            MonitoringActor());

        var worker = new ActorCapabilities("worker-1", CaseCapability.ManageFollowUp);
        aggregate = Apply(aggregate, events,
            new AcknowledgeFollowUpCommand(Guid.NewGuid(), 0, followUpId, null, RecordedAt), worker);

        var acknowledgement = Assert.IsType<CaseFollowUpAcknowledged>(events[^1]);
        Assert.Equal("worker-1", acknowledgement.ActorId);
        var followUp = Assert.Single(aggregate.FollowUps);
        Assert.Equal(FollowUpStatus.Acknowledged, followUp.Status);
        Assert.Equal(DueAt, followUp.DueAtUtc);
        Assert.Equal(followUpId, followUp.FollowUpId);
        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new AcknowledgeFollowUpCommand(Guid.NewGuid(), 0, followUpId, null, RecordedAt), worker));
    }

    [Fact]
    public void FollowUpAcknowledgement_RejectsNonAssignee()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "callback", "worker-1", DueAt, "staff scheduled", null, RecordedAt),
            MonitoringActor());

        Assert.Throws<UnauthorizedAccessException>(() => Apply(aggregate, events,
            new AcknowledgeFollowUpCommand(Guid.NewGuid(), 0, followUpId, null, RecordedAt),
            new ActorCapabilities("officer-1", CaseCapability.ManageFollowUp)));
    }

    [Fact]
    public void MissedOutcome_RetainsScheduleWithoutCompletingCare()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "revisit holding", "worker-1", DueAt, "routine check", null, RecordedAt),
            MonitoringActor());
        aggregate = Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Missed, "nobody present at holding", null, "attempt logged", null, RecordedAt),
            MonitoringActor());

        var followUp = Assert.Single(aggregate.FollowUps);
        Assert.Equal(FollowUpStatus.Missed, followUp.Status);
        Assert.Equal(DueAt, followUp.DueAtUtc);
        Assert.Equal(CaseWorkflow.Reported, aggregate.Workflow);
        var recorded = Assert.IsType<CaseFollowUpOutcomeRecorded>(events[^1]);
        Assert.Equal(DueAt, recorded.PreviousDueAtUtc);
        Assert.Null(recorded.NewDueAtUtc);
    }

    [Fact]
    public void UnreachableOutcome_DoesNotImplyRecoveryOrAbsenceOfNeed()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "contact reporter", "worker-1", DueAt, "callback", null, RecordedAt),
            MonitoringActor());
        aggregate = Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Unreachable, "phone off, holding gate locked", null, "attempt logged", null, RecordedAt),
            MonitoringActor());

        Assert.Equal(FollowUpStatus.Unreachable, Assert.Single(aggregate.FollowUps).Status);
        Assert.Equal(CaseWorkflow.Reported, aggregate.Workflow);
    }

    [Fact]
    public void MissedFollowUp_CanOnlyBeRescheduled_PreservingOldDue()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "revisit holding", "worker-1", DueAt, "routine check", null, RecordedAt),
            MonitoringActor());
        aggregate = Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Missed, "nobody present", null, "attempt logged", null, RecordedAt),
            MonitoringActor());

        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Completed, "done", null, "reason", null, RecordedAt),
            MonitoringActor()));

        aggregate = Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Rescheduled, "retry after market day", RescheduledDueAt, "new attempt", null, RecordedAt),
            MonitoringActor());

        var followUp = Assert.Single(aggregate.FollowUps);
        Assert.Equal(FollowUpStatus.Scheduled, followUp.Status);
        Assert.Equal(RescheduledDueAt, followUp.DueAtUtc);
        var recorded = Assert.IsType<CaseFollowUpOutcomeRecorded>(events[^1]);
        Assert.Equal(DueAt, recorded.PreviousDueAtUtc);
        Assert.Equal(RescheduledDueAt, recorded.NewDueAtUtc);
    }

    [Fact]
    public void RecordFollowUpOutcome_EnforcesNotesAndDueDiscipline()
    {
        var (aggregate, events) = AcceptedCase();
        var followUpId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, followUpId, "revisit", "worker-1", DueAt, "routine", null, RecordedAt),
            MonitoringActor());

        Assert.Throws<ArgumentException>(() => Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Completed, "", null, "reason", null, RecordedAt),
            MonitoringActor()));
        Assert.Throws<ArgumentException>(() => Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Completed, "done", DueAt, "reason", null, RecordedAt),
            MonitoringActor()));
        Assert.Throws<ArgumentException>(() => Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Rescheduled, "retry", null, "reason", null, RecordedAt),
            MonitoringActor()));
        Assert.Throws<ArgumentException>(() => Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, followUpId, FollowUpOutcome.Rescheduled, "retry", DueAt, "reason", null, RecordedAt),
            MonitoringActor()));
        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 0, Guid.NewGuid(), FollowUpOutcome.Completed, "done", null, "reason", null, RecordedAt),
            MonitoringActor()));
    }

    [Fact]
    public void Escalation_RaisedAcknowledgedClosed_KeepsOwnerAccountable()
    {
        var (aggregate, events) = AcceptedCase();
        var escalationId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new RaiseEscalationCommand(Guid.NewGuid(), 0, escalationId, "holding unreachable twice", ["event-ref"], "duty-officer", null, RecordedAt),
            MonitoringActor());
        Assert.Equal(EscalationStatus.Raised, Assert.Single(aggregate.Escalations).Status);

        aggregate = Apply(aggregate, events,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 0, escalationId, "supervisor paged", "acknowledged", null, RecordedAt),
            MonitoringActor());
        Assert.Equal(EscalationStatus.Acknowledged, Assert.Single(aggregate.Escalations).Status);

        aggregate = Apply(aggregate, events,
            new CloseEscalationCommand(Guid.NewGuid(), 0, escalationId, "supervisor visited holding", "resolved", null, RecordedAt),
            MonitoringActor());

        Assert.Equal(EscalationStatus.Closed, Assert.Single(aggregate.Escalations).Status);
        Assert.Equal("officer-1", aggregate.AccountableOwnerId);
    }

    [Fact]
    public void Escalation_EnforcesLifecycleOrder()
    {
        var (aggregate, events) = AcceptedCase();
        var escalationId = Guid.NewGuid();
        aggregate = Apply(aggregate, events,
            new RaiseEscalationCommand(Guid.NewGuid(), 0, escalationId, "concern", [], "duty-officer", null, RecordedAt),
            MonitoringActor());

        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new CloseEscalationCommand(Guid.NewGuid(), 0, escalationId, "resolution", "reason", null, RecordedAt),
            MonitoringActor()));
        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 0, Guid.NewGuid(), null, "reason", null, RecordedAt),
            MonitoringActor()));

        aggregate = Apply(aggregate, events,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 0, escalationId, null, "acknowledged", null, RecordedAt),
            MonitoringActor());
        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 0, escalationId, null, "again", null, RecordedAt),
            MonitoringActor()));
    }

    [Fact]
    public void MonitoringWork_IsRejectedOnResolvedCases()
    {
        var (aggregate, events) = AcceptedCase();
        var owner = new ActorCapabilities("officer-1",
            CaseCapability.ResolveCase | CaseCapability.RecordObservation | CaseCapability.ManageFollowUp | CaseCapability.ManageEscalation);
        aggregate = Apply(aggregate, events,
            new ResolveCaseCommand(Guid.NewGuid(), 0, "work complete", null, RecordedAt), owner);
        Assert.Equal(CaseWorkflow.Resolved, aggregate.Workflow);

        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new RecordObservationCommand(Guid.NewGuid(), 0, Guid.NewGuid(), "note", "visit", "reason", null, RecordedAt),
            MonitoringActor()));
        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 0, Guid.NewGuid(), "revisit", "worker-1", DueAt, "reason", null, RecordedAt),
            MonitoringActor()));
        Assert.Throws<InvalidOperationException>(() => Apply(aggregate, events,
            new RaiseEscalationCommand(Guid.NewGuid(), 0, Guid.NewGuid(), "concern", [], "duty-officer", null, RecordedAt),
            MonitoringActor()));
    }
}
