using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

public sealed class CaseNextActionsTests
{
    private static readonly DateTimeOffset AtUtc = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResolvedCaseWithReviewedRequest_HasNoActions()
    {
        var timeline = new CaseTimeline(Header(3), [
            Entry(1, "CaseReported", "Report accepted"),
            Entry(2, "CaseActionRequested", "Protected action requested"),
            Entry(3, "CaseActionRequestReviewed", "Protected action reviewed"),
            Entry(3, "CaseResolved", "Case resolved"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.Empty(actions);
    }

    [Fact]
    public void EmptyHistory_ReportsUnknownState()
    {
        var actions = CaseNextActions.Derive(new CaseTimeline(Header(0), []));

        Assert.Single(actions);
        Assert.Contains("unknown", actions[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnreviewedRequest_IsFlagged()
    {
        var timeline = new CaseTimeline(Header(2), [
            Entry(1, "CaseReported", "Report accepted"),
            Entry(2, "CaseActionRequested", "Protected action requested"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.Equal(2, actions.Count);
        Assert.Contains(actions, a => a.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(actions, a => a.Contains("check the full history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReviewedRequest_IsNotFlagged()
    {
        var timeline = new CaseTimeline(Header(2), [
            Entry(1, "CaseActionRequested", "Protected action requested"),
            Entry(2, "CaseActionRequestReviewed", "Protected action reviewed"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.DoesNotContain(actions, a => a.Contains("check the full history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReopenedCase_IsUnresolved()
    {
        var timeline = new CaseTimeline(Header(3), [
            Entry(1, "CaseReported", "Report accepted"),
            Entry(2, "CaseResolved", "Case resolved"),
            Entry(3, "CaseReopened", "Case reopened"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.Contains(actions, a => a.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
    }

    private static CaseTimelineHeader Header(long version) => new(
        Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", version, 1, AtUtc);

    private static CaseTimelineEntry Entry(long version, string type, string label) => new(
        Guid.NewGuid(), type, label, CaseTimelineEntryKind.Authoritative, version, 1,
        Guid.NewGuid(), "officer-1", null, AtUtc, 1);
}
