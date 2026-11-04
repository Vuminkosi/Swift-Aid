using SwiftAid.Contracts.Cases;

namespace SwiftAid.Ui.TestHost;

public sealed class MobileMonitoringFixture : ICaseWorkDiscoveryClient, ICaseTimelineClient, ICaseMonitoringPageClient
{
    private static readonly Guid CaseId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public Task<CaseWorkDiscoveryPage> GetAsync(CaseWorkDiscoveryKind kind, CaseWorkDiscoveryScope scope, int limit,
        Guid? cursor, CancellationToken cancellationToken = default) => Task.FromResult(new CaseWorkDiscoveryPage(CaseQueueAccess.Allowed, [CaseId]));
    public Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default,
        long? beforeAggregateVersion = null) => Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.Found,
            new CaseTimeline(new(caseId, "org", "workspace", "SZ", "Active", "fixture", 7, 1, DateTimeOffset.UtcNow), [])));
    public Task<CaseMonitoringPageResult> GetAsync(Guid caseId, int offset, int limit, long version,
        CancellationToken cancellationToken = default)
    {
        var items = Enumerable.Range(0, 30).Select(index => new CaseMonitoringPageItem(
            new MonitoringQueueItem(caseId, MonitoringQueueKind.FollowUp,
                Guid.Parse($"{index + 1:x8}-0000-0000-0000-000000000001"), $"Monitoring item {index}", "private-actor-id",
                DateTimeOffset.UnixEpoch.AddDays(index), "Scheduled", false, DateTimeOffset.UnixEpoch, 1), index % 2 == 0)).ToArray();
        return Task.FromResult(new CaseMonitoringPageResult(CaseMonitoringPageAccess.Found, new(caseId, version, items, null)));
    }
}
