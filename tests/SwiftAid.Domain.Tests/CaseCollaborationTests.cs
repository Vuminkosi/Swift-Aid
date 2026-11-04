using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Domain.Tests;

public sealed class CaseCollaborationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActorCapabilities Officer = new("officer", CaseCapability.ManageCollaborators);

    private static CaseAggregate Create() => CaseAggregate.Replay(CaseAggregate.Create(
        new AcceptCaseCommand(Guid.NewGuid(), Guid.NewGuid(),
            new ReportFacts(CaseKind.Human, Sector.HumanHealth, "Synthetic report", []), "owner", "workspace", null, Now),
        new ActorCapabilities("owner", CaseCapability.AcceptReport)));

    private static GrantCaseCollaboratorCommand Grant() => new(Guid.NewGuid(), 2, Guid.NewGuid(), "collaborator",
        CaseCollaborationActions.ReadCase | CaseCollaborationActions.RecordObservation, Now.AddHours(2),
        "Synthetic bounded collaboration", null, Now);

    [Fact]
    public void Grant_and_revocation_replay_preserves_history_owner_and_clinical_state()
    {
        var aggregate = Create();
        var command = Grant();
        var grantEvent = Assert.IsType<CaseCollaboratorGranted>(Assert.Single(aggregate.Decide(command, Officer)));
        aggregate.Apply(grantEvent);
        Assert.Equal(command.Actions, Assert.Single(aggregate.Collaborators).Actions);
        var revoke = new RevokeCaseCollaboratorCommand(Guid.NewGuid(), 3, command.GrantId,
            "Synthetic revocation", null, Now.AddMinutes(1));
        aggregate.Apply(Assert.Single(aggregate.Decide(revoke, Officer)));
        var replayed = CaseAggregate.Replay(aggregate.Events);
        Assert.Equal(revoke.RecordedAtUtc, Assert.Single(replayed.Collaborators).RevokedAtUtc);
        Assert.Equal("owner", replayed.AccountableOwnerId);
        Assert.Equal(2, replayed.AssignmentVersion);
        Assert.Equal(CaseWorkflow.Reported, replayed.Workflow);
        Assert.Equal(ClinicalClassification.Unclassified, replayed.Classification);
        Assert.Equal(4, replayed.Events.Count);
        Assert.Throws<InvalidOperationException>(() => replayed.Decide(revoke with { ExpectedVersion = 4 }, Officer));
        Assert.Throws<InvalidOperationException>(() => replayed.Decide(command with { ExpectedVersion = 4 }, Officer));
    }

    [Fact]
    public void Ownership_and_other_command_capabilities_do_not_allow_grant_or_revoke()
    {
        var aggregate = Create();
        var command = Grant();
        var owner = new ActorCapabilities("owner", CaseCapability.AssignCase | CaseCapability.AcknowledgeAssignment);
        Assert.Throws<UnauthorizedAccessException>(() => aggregate.Decide(command, owner));
        aggregate.Apply(Assert.Single(aggregate.Decide(command, Officer)));
        Assert.Throws<UnauthorizedAccessException>(() => aggregate.Decide(
            new RevokeCaseCollaboratorCommand(Guid.NewGuid(), 3, command.GrantId, "Revoke", null, Now), owner));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(65)]
    [InlineData(-1)]
    public void Missing_read_or_unsupported_actions_are_rejected(int actions)
    {
        var aggregate = Create();
        Assert.Throws<ArgumentException>(() => aggregate.Decide(Grant() with { Actions = (CaseCollaborationActions)actions }, Officer));
    }

    [Fact]
    public void Expiry_identity_reason_and_overlapping_grants_are_validated()
    {
        var aggregate = Create();
        var command = Grant();
        Assert.Throws<ArgumentException>(() => aggregate.Decide(command with { ExpiresAtUtc = Now }, Officer));
        Assert.Throws<ArgumentException>(() => aggregate.Decide(command with { ExpiresAtUtc = Now.AddHours(1).ToOffset(TimeSpan.FromHours(2)) }, Officer));
        Assert.Throws<ArgumentException>(() => aggregate.Decide(command with { GrantId = Guid.Empty }, Officer));
        Assert.Throws<ArgumentException>(() => aggregate.Decide(command with { CollaboratorActorId = " " }, Officer));
        Assert.Throws<ArgumentException>(() => aggregate.Decide(command with { Reason = " " }, Officer));
        aggregate.Apply(Assert.Single(aggregate.Decide(command, Officer)));
        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(Grant() with { ExpectedVersion = 3 }, Officer));
        Assert.Single(aggregate.Decide(Grant() with { ExpectedVersion = 3, RecordedAtUtc = Now.AddHours(3), ExpiresAtUtc = Now.AddHours(4) }, Officer));
    }

    [Fact]
    public void Replay_rejects_forged_scope_and_missing_revocation_target()
    {
        var aggregate = Create();
        var grant = Assert.IsType<CaseCollaboratorGranted>(Assert.Single(aggregate.Decide(Grant(), Officer)));
        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay(aggregate.Events.Append(grant with { Actions = (CaseCollaborationActions)128 })));
        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay(aggregate.Events.Append(grant with { ReportRevision = 2 })));
        var revoke = new CaseCollaboratorRevoked(aggregate.CaseId, 3, 1, Guid.NewGuid(), "officer", null, Now, Guid.NewGuid(), "Synthetic revoke");
        Assert.Throws<InvalidDataException>(() => CaseAggregate.Replay(aggregate.Events.Append(revoke)));
    }

    [Fact]
    public void Resolved_case_rejects_new_grants_but_allows_revocation()
    {
        var aggregate = Create();
        var grant = Grant();
        aggregate.Apply(Assert.Single(aggregate.Decide(grant, Officer)));
        aggregate.Apply(new CaseResolved(aggregate.CaseId, 4, 1, Guid.NewGuid(), "officer", null, Now, "Synthetic closure"));
        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(Grant() with
        {
            ExpectedVersion = 4, CollaboratorActorId = "another-worker"
        }, Officer));
        var revoke = new RevokeCaseCollaboratorCommand(Guid.NewGuid(), 4, grant.GrantId, "Remove access", null, Now);
        aggregate.Apply(Assert.Single(aggregate.Decide(revoke, Officer)));
        Assert.NotNull(Assert.Single(aggregate.Collaborators).RevokedAtUtc);
        Assert.Equal(CaseWorkflow.Resolved, aggregate.Workflow);
    }
}
