using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiApprovalReceiptTests
{
    private static AdminActionProposal Proposal() => new(
        "review-overdue", AdminActionKind.CreateAdministrativeReviewTask,
        new EvidenceRef("queue", "q1", "v3", null, "e1"),
        "officer-1", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        new[] { new EvidenceRef("queue", "q1", "v3", null, "e1") },
        "Three items past the recorded deadline.");

    private static EllaiApprovalService Service() =>
        new(new EllaiApprovedActionService(), new EllaiReceiptStore());

    [Fact]
    public void ApprovalRetryReturnsOneStableReceipt()
    {
        var service = Service();
        var proposal = Proposal();
        var now = DateTimeOffset.UtcNow;

        var first = service.SubmitApproval("s-1", "hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, now);
        var retry = service.SubmitApproval("s-1", "hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, now.AddMinutes(1));

        Assert.NotNull(first);
        Assert.NotNull(retry);
        Assert.Equal(first.ReceiptId, retry.ReceiptId);
        Assert.Equal(EllaiReceiptStatus.Accepted, first.Status);
    }

    [Fact]
    public void MutatedPlanYieldsNoReceipt()
    {
        var service = Service();
        var proposal = Proposal();

        Assert.Null(service.SubmitApproval("s-1", "hash-2", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExpiredApprovalVerifiesAsUnknownOutcome()
    {
        var service = Service();
        var proposal = Proposal();
        var now = DateTimeOffset.UtcNow;
        var receipt = service.SubmitApproval("s-1", "hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, now)!;

        var late = service.VerifyReceipt(receipt.ReceiptId, EllaiReceiptStatus.Applied, now.AddMinutes(16));
        Assert.NotNull(late);
        Assert.Equal(EllaiReceiptStatus.UnknownOutcome, late.Status);

        var fresh = service.VerifyReceipt(receipt.ReceiptId, EllaiReceiptStatus.Applied, now.AddMinutes(5));
        Assert.NotNull(fresh);
        Assert.Equal(EllaiReceiptStatus.Applied, fresh.Status);
    }

    [Fact]
    public void ReceiptsListBySession()
    {
        var service = Service();
        var proposal = Proposal();
        var now = DateTimeOffset.UtcNow;
        service.SubmitApproval("s-1", "hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, now);

        var store = new EllaiReceiptStore();
        Assert.Empty(store.ListBySession("s-1"));
        Assert.Null(service.VerifyReceipt("rcpt-missing", EllaiReceiptStatus.Applied, now));
    }
}
