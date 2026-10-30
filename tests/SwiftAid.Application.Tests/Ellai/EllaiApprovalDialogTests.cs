using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiApprovalDialogTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private static AdminActionProposal Proposal(string label) => new(
        label, AdminActionKind.CreateAdministrativeReviewTask,
        new EvidenceRef("queue", "q1", "v3", null, "e1"),
        "officer-1", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        new[] { new EvidenceRef("queue", "q1", "v3", null, "e1") },
        "Three items past the recorded deadline.");

    [Fact]
    public void RegisterCarriesProposalsAndDerivesStableDisplayRows()
    {
        var coordinator = new EllaiApprovalDialogCoordinator();
        var now = DateTimeOffset.UtcNow;
        var proposals = new[] { Proposal("review-overdue") };

        var (first, _) = coordinator.Register("s-1", "req-1", "hash-1", 1, now, Lifetime, proposals);
        Assert.Equal(proposals, first.Proposals);
        var row = Assert.Single(first.Actions);
        Assert.Equal("review-overdue", row.Label);
        Assert.Equal(
            EllaiApprovedActionService.ComputeActionId("hash-1", 1, "review-overdue"),
            row.ActionId);

        var edited = coordinator.ApplyEdit("req-1", "req-2", "hash-2", 2, now, Lifetime,
            new[] { Proposal("queue-reminder") });
        Assert.NotNull(edited);
        var editedRow = Assert.Single(edited.Actions);
        Assert.Equal(
            EllaiApprovedActionService.ComputeActionId("hash-2", 2, "queue-reminder"),
            editedRow.ActionId);
    }

    [Fact]
    public void RegisterRejectsOversizedPlans()
    {
        var coordinator = new EllaiApprovalDialogCoordinator();
        var proposals = new[]
        {
            Proposal("a"), Proposal("b"), Proposal("c"), Proposal("d"),
        };

        Assert.Throws<ArgumentException>(() =>
            coordinator.Register("s-1", "req-1", "hash-1", 1, DateTimeOffset.UtcNow, Lifetime, proposals));
    }

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
