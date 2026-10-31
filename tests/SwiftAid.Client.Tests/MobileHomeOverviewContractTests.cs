using SwiftAid.Client.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileHomeOverviewContractTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(-1, false)]
    [InlineData(6, false)]
    public void Query_EnforcesRecentActivityLimit(int recentActivityLimit, bool expectedValid)
    {
        Assert.Equal(expectedValid, new MobileHomeOverviewQuery(recentActivityLimit).IsValid);
    }

    [Fact]
    public void Overview_RequiresEachWorkCountKindExactlyOnce()
    {
        var overview = CreateOverview();
        var assignedCases = Count(MobileHomeWorkCountKind.AssignedCases, 3);

        var missing = overview with
        {
            WorkCounts = overview.WorkCounts.Where(item => item.Kind != MobileHomeWorkCountKind.AssignedCases).ToArray()
        };
        var duplicated = overview with
        {
            WorkCounts = overview.WorkCounts.Append(assignedCases).ToArray()
        };

        Assert.True(overview.IsValid);
        Assert.False(missing.IsValid);
        Assert.False(duplicated.IsValid);
    }

    [Fact]
    public void WorkCount_RejectsInvalidEnumsNegativeValuesAndContradictorySuppression()
    {
        Assert.False(new MobileHomeWorkCount(
            (MobileHomeWorkCountKind)99,
            MobileHomeValueState.Available,
            1).IsValid);
        Assert.False(Count(MobileHomeWorkCountKind.AssignedCases, -1).IsValid);
        Assert.False(new MobileHomeWorkCount(
            MobileHomeWorkCountKind.AssignedCases,
            MobileHomeValueState.Available,
            Value: null).IsValid);
        Assert.False(new MobileHomeWorkCount(
            MobileHomeWorkCountKind.AssignedCases,
            (MobileHomeValueState)99,
            1).IsValid);

        var suppressed = new MobileHomeWorkCount(
            MobileHomeWorkCountKind.AssignedCases,
            MobileHomeValueState.Suppressed,
            Value: null,
            Reason: MobileHomeDataReason.SmallGroupSuppression);
        var unavailable = new MobileHomeWorkCount(
            MobileHomeWorkCountKind.AssignedCases,
            MobileHomeValueState.Unavailable,
            Value: null,
            Reason: MobileHomeDataReason.SourceUnavailable);

        Assert.True(suppressed.IsValid);
        Assert.False((suppressed with { Value = 0 }).IsValid);
        Assert.False((suppressed with { Reason = MobileHomeDataReason.NotCollected }).IsValid);
        Assert.True(unavailable.IsValid);
        Assert.False((unavailable with { Value = 0 }).IsValid);
        Assert.False((unavailable with { Reason = null }).IsValid);
    }

    [Fact]
    public void Overview_RequiresDueAndOverdueFollowUpCountsToBeConsistent()
    {
        var overview = CreateOverview();
        var dueExceedsOverdue = overview with
        {
            WorkCounts = ReplaceCount(overview.WorkCounts, Count(MobileHomeWorkCountKind.OverdueFollowUps, 3))
        };
        var dueSuppressedButOverdueAvailable = overview with
        {
            WorkCounts = ReplaceCount(overview.WorkCounts,
                new MobileHomeWorkCount(MobileHomeWorkCountKind.DueFollowUps,
                    MobileHomeValueState.Suppressed, null, MobileHomeDataReason.SmallGroupSuppression),
                new MobileHomeWorkCount(MobileHomeWorkCountKind.OverdueFollowUps,
                    MobileHomeValueState.Available, 0))
        };
        var bothSuppressed = overview with
        {
            WorkCounts = ReplaceCount(overview.WorkCounts,
                new MobileHomeWorkCount(MobileHomeWorkCountKind.DueFollowUps,
                    MobileHomeValueState.Suppressed, null, MobileHomeDataReason.SmallGroupSuppression),
                new MobileHomeWorkCount(MobileHomeWorkCountKind.OverdueFollowUps,
                    MobileHomeValueState.Suppressed, null, MobileHomeDataReason.SmallGroupSuppression))
        };

        Assert.True(overview.IsValid);
        Assert.False(dueExceedsOverdue.IsValid);
        Assert.False(dueSuppressedButOverdueAvailable.IsValid);
        Assert.True(bothSuppressed.IsValid);
    }

    [Fact]
    public void Overview_EvaluatesCurrentStaleAndUnknownFreshnessStates()
    {
        var current = CreateOverview();
        var currentBeforeExpiry = current.FreshnessExpiresAtUtc!.Value.AddTicks(-1);
        var currentAtExpiry = current.FreshnessExpiresAtUtc.Value;
        var stale = current with
        {
            FreshnessState = MobileHomeFreshnessState.Stale,
            FreshnessExpiresAtUtc = current.AsOfUtc.AddMinutes(-1)
        };
        var unknown = current with
        {
            FreshnessState = MobileHomeFreshnessState.Unknown,
            FreshnessExpiresAtUtc = null
        };

        Assert.True(current.IsValid);
        Assert.Equal(MobileHomeFreshnessState.Current, current.EvaluateFreshness(currentBeforeExpiry));
        Assert.Equal(MobileHomeFreshnessState.Stale, current.EvaluateFreshness(currentAtExpiry));
        Assert.Equal(MobileHomeFreshnessState.Stale, stale.EvaluateFreshness(current.AsOfUtc));
        Assert.Equal(MobileHomeFreshnessState.Unknown, unknown.EvaluateFreshness(current.AsOfUtc));
        Assert.True(stale.IsValid);
        Assert.True(unknown.IsValid);
        Assert.False((current with { FreshnessExpiresAtUtc = current.AsOfUtc }).IsValid);
        Assert.False((stale with { FreshnessExpiresAtUtc = current.AsOfUtc.AddTicks(1) }).IsValid);
        Assert.False((unknown with { FreshnessExpiresAtUtc = current.AsOfUtc }).IsValid);
    }

    [Theory]
    [InlineData(MobileHomeActivityKind.CaseReported, MobileHomeActivityDestination.Cases)]
    [InlineData(MobileHomeActivityKind.FollowUpCompleted, MobileHomeActivityDestination.FollowUps)]
    [InlineData(MobileHomeActivityKind.AssignmentReceived, MobileHomeActivityDestination.Cases)]
    [InlineData(MobileHomeActivityKind.AssistanceProvided, MobileHomeActivityDestination.Assistance)]
    [InlineData(MobileHomeActivityKind.EscalationOpened, MobileHomeActivityDestination.Cases)]
    public void Activity_AcceptsKindSpecificAuthorizedDestinations(
        MobileHomeActivityKind kind,
        MobileHomeActivityDestination destination)
    {
        var activity = Activity(
            "activity-1",
            kind,
            CreateOverview().AsOfUtc.AddHours(-1),
            destination);

        Assert.True(activity.IsValid);
    }

    [Fact]
    public void Activity_RequiresOpaqueIdentifiersAndMatchingDestinationForKind()
    {
        var activity = Activity(
            "activity_301",
            MobileHomeActivityKind.CaseReported,
            CreateOverview().AsOfUtc.AddHours(-1),
            MobileHomeActivityDestination.Cases,
            "opaque-case-41");

        Assert.True(activity.IsValid);
        Assert.False((activity with { ActivityId = "case/301" }).IsValid);
        Assert.False((activity with { DestinationId = "case/41 with spaces" }).IsValid);
        Assert.False((activity with { Destination = MobileHomeActivityDestination.FollowUps }).IsValid);
        Assert.False((activity with { Kind = (MobileHomeActivityKind)99 }).IsValid);
        Assert.False((activity with { Destination = (MobileHomeActivityDestination)99 }).IsValid);
    }

    [Fact]
    public void Overview_RequiresAtMostFiveNewestFirstActivityItemsNotLaterThanSnapshot()
    {
        var overview = CreateOverview();
        var newest = Activity("activity-1", MobileHomeActivityKind.CaseReported,
            overview.AsOfUtc.AddHours(-2), MobileHomeActivityDestination.Cases);
        var older = Activity("activity-2", MobileHomeActivityKind.FollowUpCompleted,
            overview.AsOfUtc.AddDays(-1), MobileHomeActivityDestination.FollowUps);

        var newestFirst = overview with { RecentActivity = [newest, older] };
        var oldestFirst = overview with { RecentActivity = [older, newest] };
        var tooMany = overview with
        {
            RecentActivity = Enumerable.Range(0, 6)
                .Select(index => Activity(
                    $"activity-{index}",
                    MobileHomeActivityKind.CaseReported,
                    overview.AsOfUtc.AddMinutes(-index - 1),
                    MobileHomeActivityDestination.Cases))
                .ToArray()
        };
        var futureActivity = overview with
        {
            RecentActivity = [Activity(
                "activity-future",
                MobileHomeActivityKind.CaseReported,
                overview.AsOfUtc.AddTicks(1),
                MobileHomeActivityDestination.Cases)]
        };

        Assert.True(newestFirst.IsValid);
        Assert.False(oldestFirst.IsValid);
        Assert.False(tooMany.IsValid);
        Assert.False(futureActivity.IsValid);
    }

    private static MobileHomeOverview CreateOverview()
    {
        var asOf = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        return new MobileHomeOverview(
            VerifiedDisplayName: null,
            AuthorizedWorkspaceLabel: null,
            AuthorizedRegionLabel: null,
            AsOfUtc: asOf,
            FreshnessExpiresAtUtc: asOf.AddMinutes(5),
            FreshnessState: MobileHomeFreshnessState.Current,
            Completeness: MobileHomeCompleteness.Complete,
            WorkCounts:
            [
                Count(MobileHomeWorkCountKind.AssignedCases, 3),
                Count(MobileHomeWorkCountKind.DueFollowUps, 2),
                Count(MobileHomeWorkCountKind.OverdueFollowUps, 1),
                Count(MobileHomeWorkCountKind.UnresolvedAssistanceRequests, 1),
                Count(MobileHomeWorkCountKind.UnresolvedEscalations, 0)
            ],
            RecentActivity: []);
    }

    private static MobileHomeWorkCount Count(MobileHomeWorkCountKind kind, long value) =>
        new(kind, MobileHomeValueState.Available, value);

    private static IReadOnlyList<MobileHomeWorkCount> ReplaceCount(
        IReadOnlyList<MobileHomeWorkCount> current,
        MobileHomeWorkCount replacement) =>
        current.Select(item => item.Kind == replacement.Kind ? replacement : item).ToArray();

    private static IReadOnlyList<MobileHomeWorkCount> ReplaceCount(
        IReadOnlyList<MobileHomeWorkCount> current,
        MobileHomeWorkCount firstReplacement,
        MobileHomeWorkCount secondReplacement) =>
        ReplaceCount(ReplaceCount(current, firstReplacement), secondReplacement);

    private static MobileHomeActivity Activity(
        string activityId,
        MobileHomeActivityKind kind,
        DateTimeOffset occurredAtUtc,
        MobileHomeActivityDestination destination,
        string? destinationId = null) =>
        new(activityId, kind, occurredAtUtc, destination, destinationId);
}
