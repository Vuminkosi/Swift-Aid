using SwiftAid.Contracts.Cases;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileMonitoringQueueClientTests
{
    [Fact]
    public async Task ReadsAllCasePagesDeduplicatesCasesAndFiltersAssignedWork()
    {
        var pages = new Pages();
        var client = new MobileMonitoringQueueClient(new Discovery(), new Timelines(), pages);
        var open = await client.GetOpenAsync(null);
        Assert.Equal(CaseQueueAccess.Allowed, open.Access);
        Assert.Equal(206, open.Items.Count);
        Assert.Equal(1, open.Items.Count(item => item.Kind == MonitoringQueueKind.Escalation));
        Assert.Equal(3, pages.FirstCaseReads);
        var assigned = await client.GetAssignedAsync(null);
        Assert.Equal(CaseQueueAccess.Allowed, assigned.Access);
        Assert.Equal(103, assigned.Items.Count);
        Assert.All(assigned.Items, item => Assert.Equal(MonitoringQueueKind.FollowUp, item.Kind));
        var limited = await client.GetOpenAsync(2);
        Assert.Equal(2, limited.Items.Count);
    }

    [Fact]
    public async Task DeniedContinuationDoesNotReturnEarlierRows()
    {
        var client = new MobileMonitoringQueueClient(new Discovery(), new Timelines(), new Pages { DenyContinuation = true });
        var result = await client.GetOpenAsync(null);
        Assert.Equal(CaseQueueAccess.Denied, result.Access);
        Assert.Empty(result.Items);
    }

    private static readonly Guid First = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed class Discovery : ICaseWorkDiscoveryClient
    {
        public Task<CaseWorkDiscoveryPage> GetAsync(CaseWorkDiscoveryKind kind, CaseWorkDiscoveryScope scope,
            int limit, Guid? cursor, CancellationToken cancellationToken = default) => Task.FromResult(new CaseWorkDiscoveryPage(
                CaseQueueAccess.Allowed, kind switch
                {
                    CaseWorkDiscoveryKind.FollowUp => [First],
                    CaseWorkDiscoveryKind.Assistance => [First, Second],
                    _ => [Second]
                }));
    }

    private sealed class Timelines : ICaseTimelineClient
    {
        public Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default,
            long? beforeAggregateVersion = null) => Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.Found,
                new CaseTimeline(new(caseId, "org", "workspace", "SZ", "Active", "fixture", 7, 1, DateTimeOffset.UtcNow), [])));
    }

    private sealed class Pages : ICaseMonitoringPageClient
    {
        public int FirstCaseReads { get; private set; }
        public bool DenyContinuation { get; init; }
        public Task<CaseMonitoringPageResult> GetAsync(Guid caseId, int offset, int limit, long version,
            CancellationToken cancellationToken = default)
        {
            if (caseId == Second)
            {
                var item = new MonitoringQueueItem(caseId, MonitoringQueueKind.Escalation, Guid.NewGuid(), "Escalation", null,
                    null, "Raised", false, DateTimeOffset.UnixEpoch, 1);
                return Task.FromResult(new CaseMonitoringPageResult(CaseMonitoringPageAccess.Found, new(caseId, version, [new(item, false)], null)));
            }
            FirstCaseReads++;
            if (DenyContinuation && offset > 0) return Task.FromResult(new CaseMonitoringPageResult(CaseMonitoringPageAccess.NotFoundOrNotAuthorized, null));
            var items = Enumerable.Range(offset, Math.Min(limit, 205 - offset)).Select(index => new CaseMonitoringPageItem(
                new MonitoringQueueItem(caseId, MonitoringQueueKind.FollowUp,
                    Guid.Parse($"{index + 1:x8}-0000-0000-0000-000000000001"), "Follow-up", "worker",
                    DateTimeOffset.UnixEpoch.AddDays(index), "Scheduled", false, DateTimeOffset.UnixEpoch, 1), index % 2 == 0)).ToArray();
            return Task.FromResult(new CaseMonitoringPageResult(CaseMonitoringPageAccess.Found,
                new(caseId, version, items, offset + items.Length < 205 ? offset + limit : null)));
        }
    }
}
