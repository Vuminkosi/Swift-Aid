using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Domain.Tests;

public sealed class AssignmentAcknowledgementTests
{
    private static CaseAggregate Create()
    {
        var now = DateTimeOffset.UtcNow;
        return CaseAggregate.Replay(CaseAggregate.Create(new AcceptCaseCommand(Guid.NewGuid(), Guid.NewGuid(),
            new ReportFacts(CaseKind.Human, Sector.HumanHealth, "Synthetic observation", []),
            "owner", "workspace", now, now), new("owner", CaseCapability.AcceptReport)));
    }

    [Fact]
    public void AcknowledgementReplaysWithoutChangingWorkflowOrClinicalState()
    {
        var aggregate = Create();
        Assert.Equal(2, aggregate.AssignmentVersion);
        Assert.Null(aggregate.AssignmentAcknowledgedAtUtc);
        var command = new AcknowledgeAssignmentCommand(Guid.NewGuid(), 2, 2, null, DateTimeOffset.UtcNow);
        var events = aggregate.Decide(command, new("owner", CaseCapability.AcknowledgeAssignment));
        var acknowledged = CaseAggregate.Replay(aggregate.Events.Concat(events));
        Assert.Equal(command.RecordedAtUtc, acknowledged.AssignmentAcknowledgedAtUtc);
        Assert.Equal(CaseWorkflow.Reported, acknowledged.Workflow);
        Assert.Equal(ClinicalClassification.Unclassified, acknowledged.Classification);
        Assert.Equal(ReportedValueState.Unknown, acknowledged.Outcome.State);
        Assert.Throws<InvalidOperationException>(() => acknowledged.Decide(command with { CommandId = Guid.NewGuid(), ExpectedVersion = 3 },
            new("owner", CaseCapability.AcknowledgeAssignment)));
    }

    [Fact]
    public void ForeignOwnerMissingCapabilityAndStaleAssignmentAreRejected()
    {
        var aggregate = Create();
        var command = new AcknowledgeAssignmentCommand(Guid.NewGuid(), 2, 2, null, DateTimeOffset.UtcNow);
        Assert.Throws<UnauthorizedAccessException>(() => aggregate.Decide(command, new("other", CaseCapability.AcknowledgeAssignment)));
        Assert.Throws<UnauthorizedAccessException>(() => aggregate.Decide(command, new("owner", CaseCapability.AssignCase)));
        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(command with { AssignmentVersion = 1 }, new("owner", CaseCapability.AcknowledgeAssignment)));
        var forged = new CaseAssignmentAcknowledged(aggregate.CaseId, 3, 1, Guid.NewGuid(), "other", null, DateTimeOffset.UtcNow, 2);
        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay(aggregate.Events.Append(forged)));
    }

    [Fact]
    public void ReopeningStartsANewPendingAssignmentEvenForTheSameOwner()
    {
        var aggregate = Create();
        var now = DateTimeOffset.UtcNow;
        var events = aggregate.Events.Concat<CaseEvent>([
            new CaseAssignmentAcknowledged(aggregate.CaseId, 3, 1, Guid.NewGuid(), "owner", null, now, 2),
            new CaseResolved(aggregate.CaseId, 4, 1, Guid.NewGuid(), "owner", null, now, "Synthetic closure"),
            new CaseReopened(aggregate.CaseId, 5, 1, Guid.NewGuid(), "owner", null, now, "Synthetic reopen", "owner")]);
        var reopened = CaseAggregate.Replay(events);
        Assert.Equal(5, reopened.AssignmentVersion);
        Assert.Null(reopened.AssignmentAcknowledgedAtUtc);
        Assert.Throws<InvalidOperationException>(() => reopened.Decide(
            new AcknowledgeAssignmentCommand(Guid.NewGuid(), 5, 2, null, now), new("owner", CaseCapability.AcknowledgeAssignment)));
        Assert.Single(reopened.Decide(new AcknowledgeAssignmentCommand(Guid.NewGuid(), 5, 5, null, now),
            new("owner", CaseCapability.AcknowledgeAssignment)));
    }
}
