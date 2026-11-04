using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Domain.Tests;

public sealed class CaseDiscussionTests
{
    [Fact]
    public void Discussion_is_append_only_and_does_not_become_an_observation()
    {
        var aggregate = Create();
        var command = new PostCaseDiscussionCommand(Guid.NewGuid(), 2, "Delivery confirmed", ["officer-1"], [], DateTimeOffset.UtcNow);
        var events = aggregate.Decide(command, new ActorCapabilities("officer-1", CaseCapability.RecordObservation));
        var posted = Assert.IsType<CaseDiscussionPosted>(Assert.Single(events));
        Assert.Equal(command.CommandId, posted.CommandId);
        var replayed = CaseAggregate.Replay(aggregate.Events.Concat(events));
        Assert.Equal(3, replayed.AggregateVersion);
        Assert.Equal(0, replayed.ObservationCount);
        Assert.Equal("Delivery confirmed", replayed.Events.OfType<CaseDiscussionPosted>().Single().Message);
    }

    [Fact]
    public void Foreign_references_and_missing_capability_are_rejected()
    {
        var aggregate = Create();
        var command = new PostCaseDiscussionCommand(Guid.NewGuid(), 2, "Delivery confirmed", [], [], DateTimeOffset.UtcNow);
        Assert.Throws<UnauthorizedAccessException>(() => aggregate.Decide(command, new ActorCapabilities("officer-1", (CaseCapability)0)));
        Assert.Throws<ArgumentException>(() => aggregate.Decide(command with { EvidenceKeys = ["other-case"] },
            new ActorCapabilities("officer-1", CaseCapability.RecordObservation)));
        Assert.Throws<ArgumentException>(() => aggregate.Decide(command with { MentionReferences = ["other-actor"] },
            new ActorCapabilities("officer-1", CaseCapability.RecordObservation)));
    }

    private static CaseAggregate Create() => CaseAggregate.Replay(CaseAggregate.Create(new AcceptCaseCommand(
        Guid.NewGuid(), Guid.NewGuid(), new ReportFacts(CaseKind.Human, Sector.HumanHealth, "Synthetic report", []),
        "officer-1", "workspace-1", null, DateTimeOffset.UtcNow), new ActorCapabilities("officer-1", CaseCapability.AcceptReport)));
}
