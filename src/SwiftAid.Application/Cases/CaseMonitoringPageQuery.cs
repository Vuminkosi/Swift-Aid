using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Cases;

/// <summary>Reads case-specific work only while the supplied authorized read lease is active.</summary>
public interface ICaseMonitoringPageStore
{
    Task<IReadOnlyList<MonitoringQueueItem>> ReadAsync(ICaseCommandLease lease, int offset, int take,
        DateTimeOffset utcNow, CancellationToken cancellationToken);
}

public interface ICaseMonitoringPageQuery
{
    Task<CaseMonitoringPageResult> GetAsync(ActorContext actor, Guid caseId, int offset, int limit,
        long? expectedVersion, CancellationToken cancellationToken);
}

/// <summary>Rechecks case authorization and aggregate version on every continuation.</summary>
public sealed class CaseMonitoringPageQuery(ICaseMonitoringPageStore store, ICaseTimelineStore timelines,
    ICaseAccessPolicy policy, ICaseCommandUnitOfWork unitOfWork, TimeProvider clock) : ICaseMonitoringPageQuery
{
    public async Task<CaseMonitoringPageResult> GetAsync(ActorContext actor, Guid caseId, int offset, int limit,
        long? expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty || offset < 0 || offset > 1_000_000 || limit is < 1 or > 100 ||
            expectedVersion is <= 0 || offset > 0 && expectedVersion is null)
            throw new ArgumentOutOfRangeException(nameof(offset), "The case work page request is invalid.");

        ICaseCommandLease lease;
        try
        {
            lease = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty,
                CaseLeaseMode.Read, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return new(CaseMonitoringPageAccess.NotFoundOrNotAuthorized, null);
        }

        await using (lease.ConfigureAwait(false))
        {
            var decision = await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed) return new(CaseMonitoringPageAccess.NotFoundOrNotAuthorized, null);
            var header = await timelines.GetHeaderAsync(caseId, cancellationToken).ConfigureAwait(false);
            if (header is null || header.CaseId != caseId || header.AggregateVersion <= 0)
                return new(CaseMonitoringPageAccess.NotFoundOrNotAuthorized, null);
            if (expectedVersion is { } version && version != header.AggregateVersion)
                return new(CaseMonitoringPageAccess.Stale, null);

            var rows = await store.ReadAsync(lease, offset, limit + 1, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (rows is null || rows.Count > limit + 1 || rows.Any(row => row is null || row.CaseId != caseId || row.WorkItemId == Guid.Empty ||
                    !Enum.IsDefined(row.Kind) || row.AggregateVersion <= 0 || row.AggregateVersion > header.AggregateVersion ||
                    string.IsNullOrWhiteSpace(row.Status)) ||
                rows.Select(row => (row.Kind, row.WorkItemId)).Distinct().Count() != rows.Count)
                return new(CaseMonitoringPageAccess.Unavailable, null);
            var pageItems = rows.Take(limit).Select(row => new CaseMonitoringPageItem(
                row, string.Equals(row.AssigneeActorId, actor.ActorId, StringComparison.Ordinal))).ToArray();
            var next = rows.Count > limit ? checked(offset + limit) : (int?)null;
            if (next > 1_000_000) return new(CaseMonitoringPageAccess.Unavailable, null);
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(CaseMonitoringPageAccess.Found, new(caseId, header.AggregateVersion, pageItems, next));
        }
    }
}
