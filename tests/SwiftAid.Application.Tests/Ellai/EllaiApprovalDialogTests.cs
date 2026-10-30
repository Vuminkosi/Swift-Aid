using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiApprovalDialogTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    [Fact]
    public void DuplicateEventIdFocusesTheSameReview()
    {
        var coordinator = new EllaiApprovalDialogCoordinator();
        var now = DateTimeOffset.UtcNow;

        var (first, createdFirst) = coordinator.Register("s-1", "req-1", "hash-1", 1, now, Lifetime);
        var (second, createdSecond) = coordinator.Register("s-1", "req-1", "hash-1", 1, now, Lifetime);

        Assert.True(createdFirst);
        Assert.False(createdSecond);
        Assert.Equal(EllaiApprovalDialogState.AwaitingApproval, first.State);
        Assert.Same(first, second);
        Assert.Same(first, coordinator.TryGetActive("s-1", now));
    }

    [Fact]
    public void SecondRequestQueuesUntilFirstResolves()
    {
        var coordinator = new EllaiApprovalDialogCoordinator();
        var now = DateTimeOffset.UtcNow;

        var (first, _) = coordinator.Register("s-1", "req-1", "hash-1", 1, now, Lifetime);
        var (queued, created) = coordinator.Register("s-1", "req-2", "hash-2", 1, now, Lifetime);

        Assert.True(created);
        Assert.Equal(EllaiApprovalDialogState.AwaitingApproval, first.State);
        Assert.Equal(EllaiApprovalDialogState.Queued, queued.State);
        Assert.Same(first, coordinator.TryGetActive("s-1", now));

        var resolved = coordinator.Resolve("req-1", approved: false, now);
        Assert.Equal(EllaiApprovalDialogState.Rejected, resolved?.State);
        var promoted = coordinator.TryGetActive("s-1", now);
        Assert.NotNull(promoted);
        Assert.Equal("req-2", promoted.RequestId);
        Assert.Equal(EllaiApprovalDialogState.AwaitingApproval, promoted.State);
    }

    [Fact]
    public void EditSupersedesBindingAndRequiresNewVersion()
    {
        var coordinator = new EllaiApprovalDialogCoordinator();
        var now = DateTimeOffset.UtcNow;

        coordinator.Register("s-1", "req-1", "hash-1", 1, now, Lifetime);
        var edited = coordinator.ApplyEdit("req-1", "req-2", "hash-2", 2, now, Lifetime);

        Assert.NotNull(edited);
        Assert.Equal(2, edited.PlanVersion);
        Assert.Equal("hash-2", edited.PlanHash);
        Assert.Equal(EllaiApprovalDialogState.AwaitingApproval, edited.State);
        Assert.Equal(EllaiApprovalDialogState.Superseded, coordinator.Get("req-1")?.State);

        // The superseded binding can never resolve to an approval.
        Assert.Null(coordinator.Resolve("req-1", approved: true, now));
        Assert.Same(edited, coordinator.TryGetActive("s-1", now));
    }

    [Fact]
    public void CancelSessionClosesActiveAndQueuedWithoutPromotion()
    {
        var coordinator = new EllaiApprovalDialogCoordinator();
        var now = DateTimeOffset.UtcNow;

        coordinator.Register("s-1", "req-1", "hash-1", 1, now, Lifetime);
        coordinator.Register("s-1", "req-2", "hash-2", 1, now, Lifetime);

        Assert.Equal(2, coordinator.CancelSession("s-1", now));
        Assert.Equal(EllaiApprovalDialogState.Cancelled, coordinator.Get("req-1")?.State);
        Assert.Equal(EllaiApprovalDialogState.Cancelled, coordinator.Get("req-2")?.State);
        Assert.Null(coordinator.TryGetActive("s-1", now.AddSeconds(1)));

        // Late callbacks cannot reopen the session review.
        Assert.Null(coordinator.Resolve("req-1", approved: true, now.AddSeconds(1)));
    }

    [Fact]
    public void ExpiredReviewReleasesTheActiveSlot()
    {
        var coordinator = new EllaiApprovalDialogCoordinator();
        var now = DateTimeOffset.UtcNow;

        coordinator.Register("s-1", "req-1", "hash-1", 1, now, TimeSpan.FromMinutes(1));
        var active = coordinator.TryGetActive("s-1", now.AddMinutes(2));

        Assert.Null(active);
        Assert.Equal(EllaiApprovalDialogState.Expired, coordinator.Get("req-1")?.State);

        var (fresh, created) = coordinator.Register("s-1", "req-2", "hash-2", 1, now.AddMinutes(2), Lifetime);
        Assert.True(created);
        Assert.Equal(EllaiApprovalDialogState.AwaitingApproval, fresh.State);
    }

    [Fact]
    public void UnknownRequestIdNeverResolves()
    {
        var coordinator = new EllaiApprovalDialogCoordinator();
        Assert.Null(coordinator.Resolve("req-missing", approved: true, DateTimeOffset.UtcNow));
        Assert.Null(coordinator.ApplyEdit("req-missing", "req-new", "hash", 2, DateTimeOffset.UtcNow, Lifetime));
    }
}
