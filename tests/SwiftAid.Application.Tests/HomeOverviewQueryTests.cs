using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

/// <summary>
/// Home overview: assigned counts, follow-up consistency, assistance as not
/// collected, and recent activity ordering.
/// </summary>
public sealed class HomeOverviewQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Overview_ProjectsCountsAndOrderedActivity()
    {
        var caseId = Guid.NewGuid();
        var dashboard = new StubDashboardQuery(new DashboardAggregateResult(
            DashboardAggregateAccess.Allowed,
            [
                new DashboardAggregateCaseRow(
                    caseId,
                    Sector.HumanHealth,
                    ClinicalClassification.Confirmed,
                    CaseWorkflow.Active,
                    "ZW",
                    ["ZW-HA"],
                    Now.AddDays(-2),
                    Now.AddDays(-1))
            ],
            Now,
            false));
        var monitoring = new StubMonitoringQuery(
            assigned:
            [
                new MonitoringQueueItem(
                    caseId, MonitoringQueueKind.FollowUp, Guid.NewGuid(), "Visit", "officer-1",
                    Now.AddDays(1), "Scheduled", false, Now.AddDays(-1), 3)
            ],
            open: []);
        var store = new StubHomeStore(
            shares: [new HomeOverviewShare(Guid.NewGuid(), Now.AddDays(-1))],
            organizations: ["org-1"],
            displayName: "Officer One");
        var query = new HomeOverviewQuery(dashboard, monitoring, store, new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), 5, CancellationToken.None);

        Assert.Equal(HomeOverviewAccess.Allowed, result.Access);
        Assert.NotNull(result.Payload);
        var payload = result.Payload;
        Assert.Equal("Officer One", payload.DisplayName);
        Assert.False(payload.Partial);
        Assert.False(payload.Truncated);
        Assert.Equal(5, payload.Counts.Count);
        var byKind = payload.Counts.ToDictionary(count => count.Kind);
        Assert.Equal(1, byKind[HomeOverviewCountKind.AssignedCases].Value);
        Assert.Equal(1, byKind[HomeOverviewCountKind.DueFollowUps].Value);
        Assert.Equal(0, byKind[HomeOverviewCountKind.OverdueFollowUps].Value);
        Assert.Null(byKind[HomeOverviewCountKind.UnresolvedAssistanceRequests].Value);
        Assert.Equal(HomeOverviewCountReason.NotCollected, byKind[HomeOverviewCountKind.UnresolvedAssistanceRequests].Reason);
        Assert.Equal(0, byKind[HomeOverviewCountKind.UnresolvedEscalations].Value);
        Assert.Equal(2, payload.Activities.Count);
        Assert.True(payload.Activities[0].OccurredAtUtc >= payload.Activities[1].OccurredAtUtc);
        Assert.All(payload.Activities, activity => Assert.True(activity.OccurredAtUtc <= payload.AsOfUtc));
    }

    [Fact]
    public async Task Overview_MarksPartialWhenReadsFail()
    {
        var dashboard = new StubDashboardQuery(new DashboardAggregateResult(
            DashboardAggregateAccess.Allowed, [], Now, false));
        var monitoring = new FailingMonitoringQuery();
        var query = new HomeOverviewQuery(
            dashboard, monitoring, new StubHomeStore([], [], null), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), 5, CancellationToken.None);

        Assert.Equal(HomeOverviewAccess.Unavailable, result.Access);
        Assert.Null(result.Payload);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public async Task Overview_RejectsOutOfRangeActivityLimit(int limit)
    {
        var query = new HomeOverviewQuery(
            new StubDashboardQuery(new DashboardAggregateResult(DashboardAggregateAccess.Allowed, [], Now, false)),
            new StubMonitoringQuery([], []),
            new StubHomeStore([], [], null),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            query.GetAsync(CreateOfficer(), limit, CancellationToken.None));
    }

    private static ActorContext CreateOfficer() => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "officer-1"),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "unit-test")));

    private sealed class StubDashboardQuery(DashboardAggregateResult result) : IDashboardAggregateQuery
    {
        public Task<DashboardAggregateResult> GetAsync(
            ActorContext actor, TimeSpan window, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class StubMonitoringQuery(
        IReadOnlyList<MonitoringQueueItem> assigned,
        IReadOnlyList<MonitoringQueueItem> open) : ICaseMonitoringQueueQuery
    {
        public Task<MonitoringQueueResult> GetAssignedAsync(
            ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct) =>
            Task.FromResult(new MonitoringQueueResult(CaseQueueAccess.Allowed, assigned));

        public Task<MonitoringQueueResult> GetOpenAsync(
            ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct) =>
            Task.FromResult(new MonitoringQueueResult(CaseQueueAccess.Allowed, open));
    }

    private sealed class FailingMonitoringQuery : ICaseMonitoringQueueQuery
    {
        public Task<MonitoringQueueResult> GetAssignedAsync(
            ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct) =>
            throw new InvalidOperationException("The queue is unavailable.");

        public Task<MonitoringQueueResult> GetOpenAsync(
            ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct) =>
            throw new InvalidOperationException("The queue is unavailable.");
    }

    private sealed class StubHomeStore(
        IReadOnlyList<HomeOverviewShare> shares,
        IReadOnlyList<string> organizations,
        string? displayName) : IHomeOverviewStore
    {
        public Task<IReadOnlyList<HomeOverviewShare>> GetActiveSharesAsync(string actorId, CancellationToken ct) =>
            Task.FromResult(shares);

        public Task<IReadOnlyList<string>> GetMembershipOrganizationIdsAsync(string actorId, CancellationToken ct) =>
            Task.FromResult(organizations);

        public Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct) =>
            Task.FromResult(displayName);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
