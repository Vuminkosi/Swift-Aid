using SwiftAid.Contracts.Cases;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Builds monitoring views from complete, reauthorized case-scoped pages.</summary>
public sealed class MobileMonitoringQueueClient(ICaseWorkDiscoveryClient discovery, ICaseTimelineClient timelines,
    ICaseMonitoringPageClient pages) : IMonitoringQueueClient
{
    public Task<MonitoringQueueResult> GetAssignedAsync(int? limit, CancellationToken cancellationToken = default) =>
        ReadAsync(MonitoringQueueScope.Assigned, limit, cancellationToken);

    public Task<MonitoringQueueResult> GetOpenAsync(int? limit, CancellationToken cancellationToken = default) =>
        ReadAsync(MonitoringQueueScope.Open, limit, cancellationToken);

    private async Task<MonitoringQueueResult> ReadAsync(MonitoringQueueScope scope, int? limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 500) return new(CaseQueueAccess.Unavailable, []);
        try
        {
            var kinds = scope == MonitoringQueueScope.Assigned
                ? new[] { CaseWorkDiscoveryKind.FollowUp, CaseWorkDiscoveryKind.Assistance }
                : new[] { CaseWorkDiscoveryKind.FollowUp, CaseWorkDiscoveryKind.Escalation, CaseWorkDiscoveryKind.Assistance };
            var discoveryScope = scope == MonitoringQueueScope.Assigned ? CaseWorkDiscoveryScope.Assigned : CaseWorkDiscoveryScope.Open;
            var discovered = await Task.WhenAll(kinds.Select(kind =>
                MobileCaseWorkDiscoveryReader.ReadAsync(discovery, kind, discoveryScope, cancellationToken))).ConfigureAwait(false);
            if (discovered.Any(result => result.Access != CaseQueueAccess.Allowed))
                return new(discovered.Any(result => result.Access == CaseQueueAccess.Denied) ? CaseQueueAccess.Denied : CaseQueueAccess.Unavailable, []);

            using var throttle = new SemaphoreSlim(8);
            var requests = discovered.SelectMany(result => result.CaseIds).Distinct().Select(async caseId =>
            {
                await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await ReadCaseAsync(caseId, scope, cancellationToken).ConfigureAwait(false); }
                finally { throttle.Release(); }
            });
            var caseResults = await Task.WhenAll(requests).ConfigureAwait(false);
            if (caseResults.Any(result => result.Access != CaseQueueAccess.Allowed))
                return new(caseResults.Any(result => result.Access == CaseQueueAccess.Denied) ? CaseQueueAccess.Denied : CaseQueueAccess.Unavailable, []);
            var items = caseResults.SelectMany(result => result.Items)
                .OrderBy(item => item.DueAtUtc ?? DateTimeOffset.MaxValue).ThenBy(item => item.RecordedAtUtc)
                .ThenBy(item => item.CaseId).ThenBy(item => item.Kind).ThenBy(item => item.WorkItemId);
            return new(CaseQueueAccess.Allowed, limit is { } maximum ? items.Take(maximum).ToArray() : items.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(CaseQueueAccess.Unavailable, []); }
    }

    private async Task<MonitoringQueueResult> ReadCaseAsync(Guid caseId, MonitoringQueueScope scope, CancellationToken cancellationToken)
    {
        var timeline = await timelines.GetAsync(caseId, 1, cancellationToken).ConfigureAwait(false);
        var header = timeline.Timeline?.Header;
        if (timeline.Access != CaseTimelineAccess.Found || header is null) return new(CaseQueueAccess.Denied, []);
        if (header.CaseId != caseId || header.AggregateVersion <= 0) return new(CaseQueueAccess.Unavailable, []);
        var result = new List<MonitoringQueueItem>();
        var seen = new HashSet<(MonitoringQueueKind, Guid)>();
        var offset = 0;
        while (true)
        {
            var pageResult = await pages.GetAsync(caseId, offset, 100, header.AggregateVersion, cancellationToken).ConfigureAwait(false);
            if (pageResult.Access == CaseMonitoringPageAccess.NotFoundOrNotAuthorized) return new(CaseQueueAccess.Denied, []);
            if (pageResult.Access != CaseMonitoringPageAccess.Found || pageResult.Page is not { } page ||
                page.CaseId != caseId || page.AggregateVersion != header.AggregateVersion || page.Items is null || page.Items.Count > 100)
                return new(CaseQueueAccess.Unavailable, []);
            foreach (var item in page.Items)
            {
                if (item?.Work is not { } work || work.CaseId != caseId || work.WorkItemId == Guid.Empty ||
                    !Enum.IsDefined(work.Kind) || work.AggregateVersion <= 0 || work.AggregateVersion > header.AggregateVersion ||
                    string.IsNullOrWhiteSpace(work.Status) || !seen.Add((work.Kind, work.WorkItemId)))
                    return new(CaseQueueAccess.Unavailable, []);
                if (scope == MonitoringQueueScope.Open || item.AssignedToCurrentActor) result.Add(work);
            }
            if (page.NextOffset is not { } next) return new(CaseQueueAccess.Allowed, result);
            if (next != offset + 100 || page.Items.Count != 100 || next > 1_000_000) return new(CaseQueueAccess.Unavailable, []);
            offset = next;
        }
    }
}
