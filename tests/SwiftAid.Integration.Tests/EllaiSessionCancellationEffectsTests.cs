using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiSessionCancellationEffectsTests
{
    [Fact]
    public void SharedCancellationEffectsCloseReviewAndJournalOnlyOneEvent()
    {
        var dialogs = new EllaiApprovalDialogCoordinator();
        var journals = new EllaiSessionEventJournal();
        var now = DateTimeOffset.UtcNow;
        dialogs.Register("session-1", "review-1", "plan-hash", 1,
            now, TimeSpan.FromMinutes(5));

        EllaiSessionCancellationEffects.Apply("session-1", now, dialogs, journals);
        EllaiSessionCancellationEffects.Apply("session-1", now.AddSeconds(1), dialogs, journals);

        Assert.Equal(EllaiApprovalDialogState.Cancelled, dialogs.Get("review-1")!.State);
        var events = journals.ReadSince("session-1", 0).Events;
        var cancelled = Assert.Single(events);
        Assert.Equal("session-cancelled", cancelled.Kind);
    }
}
