using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiApprovedActionTests
{
    private static AdminActionProposal Proposal() => new(
        "review-overdue", AdminActionKind.CreateAdministrativeReviewTask,
        new EvidenceRef("queue", "q1", "v3", null, "e1"),
        "officer-1", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        new[] { new EvidenceRef("queue", "q1", "v3", null, "e1") },
        "Three items past the recorded deadline.");

    [Fact]
    public void TimeoutAfterCommitReturnsOneStableReceipt()
    {
        var service = new EllaiApprovedActionService();
        var proposal = Proposal();

        var first = service.Submit("hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc);
        var retry = service.Submit("hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc);

        Assert.NotNull(first);
        Assert.NotNull(retry);
        Assert.Equal(first.ActionId, retry.ActionId);
        Assert.Equal(first.IdempotencyKey, retry.IdempotencyKey);
    }

    [Fact]
    public void MutatedPlanInvalidatesApproval()
    {
        var service = new EllaiApprovedActionService();
        var proposal = Proposal();

        Assert.Null(service.Submit("hash-2", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc));
        Assert.Null(service.Submit("hash-1", 1, proposal, "hash-1", 1,
            "officer-2", proposal.HumanSuppliedDueAtUtc));
        Assert.Null(service.Submit("hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc?.AddHours(1)));
    }

    [Fact]
    public void ManifestScopeChangeRequiresFreshGrant()
    {
        var director = new EllaiScenarioDirector();
        var manifest = new DemoManifest(
            "m-1", "demo-morning", "scenario-1", new[] { "cases.read" },
            new[] { "case-1" }, 8, 0,
            DateTimeOffset.UtcNow.AddHours(1), new[] { "officer" }, "hash-m");

        var redeemed = director.RedeemCycle(manifest, "demo-morning", DateTimeOffset.UtcNow);
        Assert.NotNull(redeemed);
        Assert.Equal(1, redeemed.CyclesRedeemed);

        Assert.Null(director.RedeemCycle(manifest, "other-purpose", DateTimeOffset.UtcNow));

        var exhausted = manifest with { CyclesRedeemed = 8 };
        Assert.Null(director.RedeemCycle(exhausted, "demo-morning", DateTimeOffset.UtcNow));
    }
}
