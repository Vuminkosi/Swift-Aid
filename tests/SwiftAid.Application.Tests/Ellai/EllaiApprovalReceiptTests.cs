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
    public async Task ApprovalRetryReturnsOneStableReceipt()
    {
        var service = Service();
        var proposal = Proposal();
        var now = DateTimeOffset.UtcNow;

        var first = await service.SubmitApprovalAsync("s-1", "hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, now);
        var retry = await service.SubmitApprovalAsync("s-1", "hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, now.AddMinutes(1));

        Assert.NotNull(first);
        Assert.NotNull(retry);
        Assert.Equal(first.ReceiptId, retry.ReceiptId);
        Assert.Equal(EllaiReceiptStatus.Accepted, first.Status);
        Assert.Equal("officer-1", first.ApprovedRecipient);
        Assert.Equal(proposal.HumanSuppliedDueAtUtc, first.ApprovedDueAtUtc);
    }

    [Fact]
    public async Task MutatedPlanYieldsNoReceipt()
    {
        var service = Service();
        var proposal = Proposal();

        Assert.Null(await service.SubmitApprovalAsync("s-1", "hash-2", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ConflictingActionBindingIsRejected()
    {
        var receipts = new EllaiReceiptStore();
        var now = DateTimeOffset.UtcNow;

        await receipts.RecordAsync("s-1", "action-1", "key-1", "hash-1", 1,
            null, null, now, TimeSpan.FromMinutes(15), string.Empty, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receipts.RecordAsync("s-1", "action-1", "key-2", "hash-1", 1,
                null, null, now, TimeSpan.FromMinutes(15), string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredApprovalVerifiesAsUnknownOutcome()
    {
        var service = Service();
        var proposal = Proposal();
        var now = DateTimeOffset.UtcNow;
        var receipt = (await service.SubmitApprovalAsync("s-1", "hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, now))!;

        var late = await service.VerifyReceiptAsync(receipt.ReceiptId, EllaiReceiptStatus.Applied, now.AddMinutes(16));
        Assert.NotNull(late);
        Assert.Equal(EllaiReceiptStatus.UnknownOutcome, late.Status);

        var fresh = await service.VerifyReceiptAsync(receipt.ReceiptId, EllaiReceiptStatus.Applied, now.AddMinutes(5));
        Assert.NotNull(fresh);
        Assert.Equal(EllaiReceiptStatus.Applied, fresh.Status);
    }

    [Fact]
    public async Task ReceiptsListBySession()
    {
        var service = Service();
        var proposal = Proposal();
        var now = DateTimeOffset.UtcNow;
        await service.SubmitApprovalAsync("s-1", "hash-1", 1, proposal, "hash-1", 1,
            "officer-1", proposal.HumanSuppliedDueAtUtc, now);

        var store = new EllaiReceiptStore();
        Assert.Empty(await store.ListBySessionAsync("s-1", CancellationToken.None));
        Assert.Null(await service.VerifyReceiptAsync("rcpt-missing", EllaiReceiptStatus.Applied, now));
    }
}
