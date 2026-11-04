using SwiftAid.Client.Cases;

namespace SwiftAid.Mobile.Services.Cases;

/// <summary>
/// Mobile view-layer bridge over the durable case-command outbox. The case detail
/// page keeps its existing <c>ICaseCommandClient</c> panel wiring; this monitor lets
/// native UI surfaces (badges, pending banners, retry prompts) mark queued intents
/// and terminal outcomes back to staff without touching the shared panel or the
/// wire contract. It never carries payloads, keys, or report content.
/// </summary>
public sealed class CaseCommandOutboxMonitor(ICaseCommandOutboxTracker tracker)
{
    /// <summary>Pending (queued, sending, or scheduled-for-retry) intents for one case.</summary>
    public async Task<IReadOnlyList<CaseCommandOutboxEntry>> GetPendingForCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        var entries = await tracker.GetForCaseAsync(caseId, cancellationToken).ConfigureAwait(false);
        return entries
            .Where(item => item.Outcome == CaseCommandOutboxOutcome.Pending)
            .ToArray();
    }

    /// <summary>Latest known outcome for one command id issued by the offline client.</summary>
    public Task<CaseCommandOutboxEntry?> GetOutcomeAsync(
        Guid commandId,
        CancellationToken cancellationToken = default) =>
        tracker.GetAsync(commandId, cancellationToken);

    /// <summary>All retained entries for one case, pending first, for status rendering.</summary>
    public async Task<IReadOnlyList<CaseCommandOutboxEntry>> GetHistoryForCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        var entries = await tracker.GetForCaseAsync(caseId, cancellationToken).ConfigureAwait(false);
        return entries
            .OrderBy(item => item.Outcome != CaseCommandOutboxOutcome.Pending)
            .ThenBy(item => item.CommandId)
            .ToArray();
    }
}
