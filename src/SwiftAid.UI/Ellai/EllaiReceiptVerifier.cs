using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai;

/// <summary>Post-approval reconciliation rule (RT-60): every human-selected
/// action id must re-read from the durable authority with a definitive
/// status. <c>UnknownOutcome</c> is not verification, and an absent or
/// unreadable list only means the check is pending — never proof that an
/// effect did or did not occur. Verification claims recording, never that
/// dispatch succeeded.</summary>
public static class EllaiReceiptVerifier
{
    public static bool IsReconciled(
        IReadOnlyList<string> selectedActionIds,
        IReadOnlyList<EllaiApprovedReceiptDto>? receipts)
    {
        if (receipts is null || selectedActionIds.Count == 0)
        {
            return false;
        }

        return selectedActionIds.All(id => receipts.Any(receipt =>
            string.Equals(receipt.ActionId, id, StringComparison.Ordinal)
            && !string.Equals(receipt.Status, "UnknownOutcome", StringComparison.OrdinalIgnoreCase)));
    }
}
