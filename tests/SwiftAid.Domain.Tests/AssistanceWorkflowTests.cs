using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Domain.Tests;

public sealed class AssistanceWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static ActorCapabilities Worker(string id = "worker") => new(id, CaseCapability.ManageAssistance);
    private static CaseAggregate Create(CaseKind kind = CaseKind.Human) => CaseAggregate.Replay(CaseAggregate.Create(
        new AcceptCaseCommand(Guid.NewGuid(), Guid.NewGuid(), new ReportFacts(kind,
            kind == CaseKind.Human ? Sector.HumanHealth : Sector.AnimalHealth, "Synthetic report", []), "owner", "workspace", null, Now),
        new ActorCapabilities("owner", CaseCapability.AcceptReport)));
    private static Guid Request(CaseAggregate aggregate)
    {
        var id = Guid.NewGuid();
        aggregate.Apply(Assert.Single(aggregate.Decide(new RequestAssistanceCommand(Guid.NewGuid(), aggregate.AggregateVersion,
            id, "Synthetic transport request", "responsible-worker", null, null, Now), Worker("requester"))));
        return id;
    }
    private static UpdateAssistanceCommand Update(CaseAggregate aggregate, Guid id, AssistanceStatus status) =>
        new(Guid.NewGuid(), aggregate.AggregateVersion, id, status, "Synthetic recorded activity",
            status == AssistanceStatus.ReceiptConfirmed ? "Worker records recipient statement" : null,
            status == AssistanceStatus.ReceiptConfirmed ? "Synthetic telephone report; not independently verified" : null, null, Now);

    [Fact]
    public void Action_owner_change_TransfersOpenAssistanceAndPreservesRequesterOnReplay()
    {
        var aggregate = Create();
        var id = Request(aggregate);
        var command = new ChangeActionOwnerCommand(Guid.NewGuid(), aggregate.AggregateVersion,
            ActionOwnerKind.Assistance, id, "responsible-worker-2", "coverage transfer", null, Now);
        Assert.Throws<UnauthorizedAccessException>(() => aggregate.Decide(command,
            new ActorCapabilities("officer", CaseCapability.ManageFollowUp)));

        aggregate.Apply(Assert.Single(aggregate.Decide(command, Worker("officer"))));

        var state = Assert.Single(CaseAggregate.Replay(aggregate.Events).Assistance);
        Assert.Equal("responsible-worker-2", state.ResponsibleActorId);
        Assert.Equal("requester", state.RequestedByActorId);
        Assert.IsType<ActionOwnerChanged>(aggregate.Events[^1]);
    }

    [Theory]
    [InlineData(CaseKind.Human)]
    [InlineData(CaseKind.Animal)]
    public void Complete_lifecycle_preserves_owner_actors_and_receipt_provenance_on_replay(CaseKind kind)
    {
        var aggregate = Create(kind);
        var id = Request(aggregate);
        foreach (var status in new[] { AssistanceStatus.Accepted, AssistanceStatus.InProgress, AssistanceStatus.Provided })
            aggregate.Apply(Assert.Single(aggregate.Decide(Update(aggregate, id, status), Worker(status.ToString()))));
        Assert.True(Assert.Single(aggregate.Assistance).ReceiptOutstanding);
        Assert.Null(Assert.Single(aggregate.Assistance).ReceiptRecordedByActorId);
        aggregate.Apply(Assert.Single(aggregate.Decide(Update(aggregate, id, AssistanceStatus.ReceiptConfirmed), Worker("reporting-worker"))));
        var replay = CaseAggregate.Replay(aggregate.Events);
        var state = Assert.Single(replay.Assistance);
        Assert.Equal(AssistanceStatus.ReceiptConfirmed, state.Status);
        Assert.False(state.ReceiptOutstanding);
        Assert.Equal("requester", state.RequestedByActorId);
        Assert.Equal("responsible-worker", state.ResponsibleActorId);
        Assert.Equal("Accepted", state.AcceptedByActorId);
        Assert.Equal("Provided", state.ProvidedByActorId);
        Assert.Equal("reporting-worker", state.ReceiptRecordedByActorId);
        Assert.Contains("not independently verified", state.ReceiptContext!);
        Assert.Equal("owner", replay.AccountableOwnerId);
        Assert.Equal(ClinicalClassification.Unclassified, replay.Classification);
    }

    [Theory]
    [InlineData(AssistanceStatus.Cancelled)]
    [InlineData(AssistanceStatus.Unfulfilled)]
    public void Terminal_outcomes_require_reason_remain_visible_and_cannot_be_reopened_silently(AssistanceStatus status)
    {
        var aggregate = Create();
        var id = Request(aggregate);
        var command = Update(aggregate, id, status);
        Assert.Throws<ArgumentException>(() => aggregate.Decide(command with { Note = " " }, Worker()));
        aggregate.Apply(Assert.Single(aggregate.Decide(command, Worker())));
        Assert.Equal(status, Assert.Single(CaseAggregate.Replay(aggregate.Events).Assistance).Status);
        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(Update(aggregate, id, AssistanceStatus.Accepted), Worker()));
    }

    [Fact]
    public void Resolution_preserves_unfinished_assistance_and_requires_reopen_before_new_activity()
    {
        var aggregate = Create();
        var id = Request(aggregate);
        aggregate.Apply(Assert.Single(aggregate.Decide(new ResolveCaseCommand(Guid.NewGuid(), aggregate.AggregateVersion,
            "Synthetic closure reason", null, Now), new("officer", CaseCapability.ResolveCase))));
        Assert.Equal(AssistanceStatus.Requested, Assert.Single(aggregate.Assistance).Status);
        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(Update(aggregate, id, AssistanceStatus.Accepted), Worker()));
        Assert.Throws<InvalidOperationException>(() => Request(aggregate));
        Assert.Equal(AssistanceStatus.Requested, Assert.Single(CaseAggregate.Replay(aggregate.Events).Assistance).Status);
    }

    [Fact]
    public void Invalid_replay_cannot_turn_provision_into_receipt_or_change_existing_state()
    {
        var aggregate = Create();
        var id = Request(aggregate);
        var accepted = Assert.IsType<AssistanceAccepted>(Assert.Single(aggregate.Decide(Update(aggregate, id, AssistanceStatus.Accepted), Worker())));
        Assert.Throws<InvalidDataException>(() => aggregate.Apply(accepted with { Status = AssistanceStatus.ReceiptConfirmed }));
        Assert.Equal(3, aggregate.AggregateVersion);
        Assert.Equal(AssistanceStatus.Requested, Assert.Single(aggregate.Assistance).Status);
        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(Update(aggregate, Guid.NewGuid(), AssistanceStatus.Accepted), Worker()));
        Assert.Throws<ArgumentException>(() => aggregate.Decide(Update(aggregate, id, AssistanceStatus.Accepted) with { ReceiptMethod = "A shared referral link" }, Worker()));
    }

    [Fact]
    public void Rejects_missing_authority_stale_version_skipped_delivery_and_missing_receipt_context()
    {
        var aggregate = Create();
        var id = Request(aggregate);
        var accepted = Update(aggregate, id, AssistanceStatus.Accepted);
        Assert.Throws<UnauthorizedAccessException>(() => aggregate.Decide(accepted, new("owner", CaseCapability.AssignCase)));
        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(accepted with { ExpectedVersion = 2 }, Worker()));
        Assert.Throws<InvalidOperationException>(() => aggregate.Decide(Update(aggregate, id, AssistanceStatus.ReceiptConfirmed), Worker()));
        foreach (var status in new[] { AssistanceStatus.Accepted, AssistanceStatus.InProgress, AssistanceStatus.Provided })
            aggregate.Apply(Assert.Single(aggregate.Decide(Update(aggregate, id, status), Worker())));
        Assert.Throws<ArgumentException>(() => aggregate.Decide(Update(aggregate, id, AssistanceStatus.ReceiptConfirmed) with { ReceiptContext = null }, Worker()));
        Assert.True(Assert.Single(aggregate.Assistance).ReceiptOutstanding);
    }
}
