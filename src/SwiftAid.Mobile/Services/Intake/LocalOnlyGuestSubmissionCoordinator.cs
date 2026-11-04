using SwiftAid.Client.Intake;

namespace SwiftAid.Mobile.Services.Intake;

/// <summary>Leaves queued reports local when no Gateway address is configured; this adapter never submits or refreshes receipts.</summary>
public sealed class LocalOnlyGuestSubmissionCoordinator(IGuestSubmissionPersistence persistence) : IGuestSubmissionCoordinator
{
    private readonly IGuestSubmissionPersistence _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));

    public async Task<GuestSyncSummary> SubmitPendingAsync(CancellationToken cancellationToken = default)
    {
        var records = await _persistence.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var queued = records.Count(record => record.State == GuestSubmissionLocalState.Queued);
        var needsAttention = records.Count(record => record.State == GuestSubmissionLocalState.NeedsAttention);
        return new GuestSyncSummary(queued, Received: 0, RetryPending: queued, NeedsAttention: needsAttention);
    }

    public async Task<GuestReceiptRefreshSummary> RefreshReceiptsAsync(CancellationToken cancellationToken = default)
    {
        var records = await _persistence.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var received = records.Count(record => record.State == GuestSubmissionLocalState.Received && record.Receipt is not null);
        return new GuestReceiptRefreshSummary(Updated: 0, RetryPending: received);
    }
}
