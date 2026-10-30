using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiReceiptVerifierTests
{
    private static IReadOnlyList<EllaiApprovedReceiptDto> Receipts(params (string Action, string Status)[] rows) =>
        rows.Select(row => new EllaiApprovedReceiptDto($"rcpt-{row.Action}", row.Action, row.Status)).ToList();

    [Fact]
    public void DefinitiveStatusesReconcile()
    {
        Assert.True(EllaiReceiptVerifier.IsReconciled(
            new[] { "a-1", "a-2" },
            Receipts(("a-1", "Accepted"), ("a-2", "Applied"))));
    }

    [Fact]
    public void UnknownOutcomeIsNotVerification()
    {
        Assert.False(EllaiReceiptVerifier.IsReconciled(
            new[] { "a-1" },
            Receipts(("a-1", "UnknownOutcome"))));
    }

    [Fact]
    public void MissingActionIsNotVerification()
    {
        Assert.False(EllaiReceiptVerifier.IsReconciled(
            new[] { "a-1", "a-2" },
            Receipts(("a-1", "Accepted"))));
    }

    [Fact]
    public void UnavailableListIsPendingNotProof()
    {
        Assert.False(EllaiReceiptVerifier.IsReconciled(new[] { "a-1" }, null));
        Assert.False(EllaiReceiptVerifier.IsReconciled(
            new[] { "a-1" }, Array.Empty<EllaiApprovedReceiptDto>()));
    }

    [Fact]
    public void EmptySelectionNeverVerifies()
    {
        Assert.False(EllaiReceiptVerifier.IsReconciled(
            Array.Empty<string>(), Receipts(("a-1", "Accepted"))));
    }
}
