using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>One active case share granting the actor access.</summary>
public sealed record HomeOverviewShare(
    Guid CaseId,
    DateTimeOffset GrantedAtUtc);

/// <summary>Persistence port for the home overview read.</summary>
public interface IHomeOverviewStore
{
    Task<IReadOnlyList<HomeOverviewShare>> GetActiveSharesAsync(string actorId, CancellationToken ct);

    Task<IReadOnlyList<string>> GetMembershipOrganizationIdsAsync(string actorId, CancellationToken ct);

    Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct);
}

public interface IHomeOverviewQuery
{
    Task<HomeOverviewResult> GetAsync(
        ActorContext actor,
        int recentActivityLimit,
        CancellationToken ct);
}

/// <summary>
/// Home overview over cases owned by the calling officer that the caller may
/// still read, plus assigned follow-ups, readable-scope escalations, and
/// active shares. Per-case <c>ReadCase</c> enforcement is inherited from the
/// aggregate and monitoring queries; denied, missing, and vanished cases are
/// skipped and never listed. Assistance requests have no backing store and
/// are reported as not collected rather than zero.
/// </summary>
public sealed class HomeOverviewQuery(
    IDashboardAggregateQuery dashboardQuery,
    ICaseMonitoringQueueQuery monitoringQuery,
    IHomeOverviewStore store,
    TimeProvider timeProvider) : IHomeOverviewQuery
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(365);
    private static readonly TimeSpan FreshnessLifetime = TimeSpan.FromMinutes(5);
    private const int MonitoringTake = 500;

    private readonly IDashboardAggregateQuery _dashboardQuery =
        dashboardQuery ?? throw new ArgumentNullException(nameof(dashboardQuery));
    private readonly ICaseMonitoringQueueQuery _monitoringQuery =
        monitoringQuery ?? throw new ArgumentNullException(nameof(monitoringQuery));
    private readonly IHomeOverviewStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<HomeOverviewResult> GetAsync(
        ActorContext actor,
        int recentActivityLimit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (recentActivityLimit < 0 || recentActivityLimit > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(recentActivityLimit), "The activity limit must be between 0 and 5.");
        }

        var now = _timeProvider.GetUtcNow();

        DashboardAggregateResult aggregate;
        IReadOnlyList<HomeOverviewShare> shares;
        IReadOnlyList<string> organizations;
        string? displayName;
        try
        {
            aggregate = await _dashboardQuery.GetAsync(actor, Window, ct).ConfigureAwait(false);
            shares = await _store.GetActiveSharesAsync(actor.ActorId, ct).ConfigureAwait(false);
            organizations = await _store.GetMembershipOrganizationIdsAsync(actor.ActorId, ct).ConfigureAwait(false);
            displayName = await _store.GetDisplayNameAsync(actor.ActorId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new HomeOverviewResult(HomeOverviewAccess.Unavailable, null);
        }

        if (aggregate.Access != DashboardAggregateAccess.Allowed)
        {
            return new HomeOverviewResult(HomeOverviewAccess.Unavailable, null);
        }

        MonitoringQueueResult assigned;
        MonitoringQueueResult open;
        try
        {
            assigned = await _monitoringQuery.GetAssignedAsync(actor, MonitoringTake, now, ct).ConfigureAwait(false);
            open = await _monitoringQuery.GetOpenAsync(actor, MonitoringTake, now, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new HomeOverviewResult(HomeOverviewAccess.Unavailable, null);
        }

        var owned = aggregate.Rows
            .Where(row => row.CaseId != Guid.Empty)
            .ToList();
        var assignedCases = owned.Count(row => row.Workflow != CaseWorkflow.Resolved);

        var followUps = assigned.Access == CaseQueueAccess.Allowed
            ? assigned.Items.Where(item => item.Kind == MonitoringQueueKind.FollowUp).ToList()
            : null;
        var escalations = open.Access == CaseQueueAccess.Allowed
            ? open.Items.Where(item => item.Kind == MonitoringQueueKind.Escalation).ToList()
            : null;
        var assistance = open.Access == CaseQueueAccess.Allowed
            ? open.Items.Where(item => item.Kind == MonitoringQueueKind.Assistance).ToList()
            : null;

        var counts = new List<HomeOverviewCount>
        {
            new(HomeOverviewCountKind.AssignedCases, assignedCases, null),
            followUps is null
                ? new(HomeOverviewCountKind.DueFollowUps, null, HomeOverviewCountReason.SourceUnavailable)
                : new(HomeOverviewCountKind.DueFollowUps, followUps.Count, null),
            followUps is null
                ? new(HomeOverviewCountKind.OverdueFollowUps, null, HomeOverviewCountReason.SourceUnavailable)
                : new(HomeOverviewCountKind.OverdueFollowUps, followUps.Count(item => item.IsOverdue), null),
            assistance is null
                ? new(HomeOverviewCountKind.UnresolvedAssistanceRequests, null, HomeOverviewCountReason.SourceUnavailable)
                : new(HomeOverviewCountKind.UnresolvedAssistanceRequests, assistance.Count, null),
            escalations is null
                ? new(HomeOverviewCountKind.UnresolvedEscalations, null, HomeOverviewCountReason.SourceUnavailable)
                : new(HomeOverviewCountKind.UnresolvedEscalations, escalations.Count, null)
        };

        var activities = BuildActivities(owned, shares, escalations, now, recentActivityLimit);
        var partial = aggregate.Truncated || followUps is null || escalations is null || assistance is null;

        return new HomeOverviewResult(
            HomeOverviewAccess.Allowed,
            new HomeOverviewPayload(
                string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                organizations.Count == 0 ? null : $"{organizations.Count} organization(s)",
                now,
                now.Add(FreshnessLifetime),
                partial,
                aggregate.Truncated,
                counts,
                activities));
    }

    private static IReadOnlyList<HomeOverviewActivity> BuildActivities(
        IReadOnlyList<DashboardAggregateCaseRow> owned,
        IReadOnlyList<HomeOverviewShare> shares,
        IReadOnlyList<MonitoringQueueItem>? escalations,
        DateTimeOffset asOfUtc,
        int limit)
    {
        if (limit == 0)
        {
            return Array.Empty<HomeOverviewActivity>();
        }

        var ownedIds = owned.Select(row => row.CaseId).ToHashSet();
        var candidates = new List<HomeOverviewActivity>();

        candidates.AddRange(owned
            .Where(row => row.CreatedAtUtc <= asOfUtc)
            .Select(row => new HomeOverviewActivity(
                "reported-" + row.CaseId.ToString("N"),
                HomeOverviewActivityKind.CaseReported,
                row.CreatedAtUtc,
                row.CaseId)));

        candidates.AddRange(shares
            .Where(share => share.CaseId != Guid.Empty && share.GrantedAtUtc <= asOfUtc)
            .Select(share => new HomeOverviewActivity(
                "assign-" + share.CaseId.ToString("N"),
                HomeOverviewActivityKind.AssignmentReceived,
                share.GrantedAtUtc,
                share.CaseId)));

        if (escalations is not null)
        {
            candidates.AddRange(escalations
                .Where(item => item.CaseId != Guid.Empty && item.RecordedAtUtc <= asOfUtc)
                .Select(item => new HomeOverviewActivity(
                    "escalation-" + item.WorkItemId.ToString("N"),
                    HomeOverviewActivityKind.EscalationOpened,
                    item.RecordedAtUtc,
                    item.CaseId)));
        }

        return candidates
            .OrderByDescending(activity => activity.OccurredAtUtc)
            .ThenBy(activity => activity.ActivityId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }
}
