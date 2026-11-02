using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>One monitoring candidate row with its journal position.</summary>
public sealed record MonitoringQueueCandidate(
    Guid CaseId,
    string Kind,
    Guid WorkItemId,
    string Summary,
    string? AssigneeActorId,
    DateTimeOffset? DueAtUtc,
    string Status,
    DateTimeOffset RecordedAtUtc,
    long AggregateVersion);

/// <summary>Persistence port for the monitoring queue. Rows arrive due-first.</summary>
public interface ICaseMonitoringQueueStore
{
    Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssignedFollowUpsAsync(
        string assigneeActorId, int take, CancellationToken ct);

    Task<IReadOnlyList<MonitoringQueueCandidate>> GetOpenFollowUpsAsync(
        int take, CancellationToken ct);

    Task<IReadOnlyList<MonitoringQueueCandidate>> GetRaisedEscalationsAsync(
        int take, CancellationToken ct);

    Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssignedAssistanceAsync(
        string actorId, int take, CancellationToken ct);

    Task<IReadOnlyList<MonitoringQueueCandidate>> GetOpenAssistanceAsync(
        int take, CancellationToken ct);
}

public interface ICaseMonitoringQueueQuery
{
    Task<MonitoringQueueResult> GetAssignedAsync(
        ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct);

    Task<MonitoringQueueResult> GetOpenAsync(
        ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct);
}

/// <summary>
/// Authorized monitoring queue. Lists open follow-ups (assigned or all) and raised
/// escalations ordered by urgency, then enforces <c>ReadCase</c> through the shared
/// case resource policy inside one read-mode lease per returned case. Denied,
/// missing, and vanished cases are skipped and never listed.
/// </summary>
public sealed class CaseMonitoringQueueQuery(
    ICaseMonitoringQueueStore store,
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork) : ICaseMonitoringQueueQuery
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    private readonly ICaseMonitoringQueueStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ICaseAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ICaseCommandUnitOfWork _unitOfWork =
        unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    public async Task<MonitoringQueueResult> GetAssignedAsync(
        ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var take = Take(limit);
        IReadOnlyList<MonitoringQueueCandidate> candidates;
        try
        {
            var followUps = await _store.GetAssignedFollowUpsAsync(actor.ActorId, take, ct).ConfigureAwait(false);
            var assistance = await _store.GetAssignedAssistanceAsync(actor.ActorId, take, ct).ConfigureAwait(false);
            candidates = followUps.Concat(assistance).OrderBy(candidate => candidate.DueAtUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(candidate => candidate.RecordedAtUtc).Take(take).ToArray();
        }
        catch (Exception)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }

        return await FilterReadableAsync(actor, candidates, utcNow, ct).ConfigureAwait(false);
    }

    public async Task<MonitoringQueueResult> GetOpenAsync(
        ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var take = Take(limit);
        IReadOnlyList<MonitoringQueueCandidate> followUps;
        IReadOnlyList<MonitoringQueueCandidate> escalations;
        IReadOnlyList<MonitoringQueueCandidate> assistance;
        try
        {
            followUps = await _store.GetOpenFollowUpsAsync(take, ct).ConfigureAwait(false);
            escalations = await _store.GetRaisedEscalationsAsync(take, ct).ConfigureAwait(false);
            assistance = await _store.GetOpenAssistanceAsync(take, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }

        var combined = followUps.Concat(escalations).Concat(assistance)
            .OrderBy(candidate => candidate.DueAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(candidate => candidate.RecordedAtUtc).Take(take).ToArray();
        return await FilterReadableAsync(actor, combined, utcNow, ct).ConfigureAwait(false);
    }

    private async Task<MonitoringQueueResult> FilterReadableAsync(
        ActorContext actor,
        IReadOnlyList<MonitoringQueueCandidate> candidates,
        DateTimeOffset utcNow,
        CancellationToken ct)
    {
        var items = new List<MonitoringQueueItem>();
        foreach (var candidate in candidates)
        {
            if (candidate.CaseId == Guid.Empty || candidate.WorkItemId == Guid.Empty)
            {
                continue;
            }

            if (!Enum.TryParse<MonitoringQueueKind>(candidate.Kind, ignoreCase: false, out var kind))
            {
                continue;
            }

            ICaseCommandLease lease;
            try
            {
                lease = await _unitOfWork.BeginCaseAsync(
                    actor, candidate.CaseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                continue;
            }

            await using (lease.ConfigureAwait(false))
            {
                var decision = await _policy.AuthorizeAsync(actor, candidate.CaseId, CaseOperation.ReadCase, ct).ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    continue;
                }

                await lease.CommitAsync(ct).ConfigureAwait(false);
                var overdue = kind == MonitoringQueueKind.FollowUp &&
                    string.Equals(candidate.Status, nameof(FollowUpStatus.Scheduled), StringComparison.Ordinal) &&
                    candidate.DueAtUtc.HasValue &&
                    candidate.DueAtUtc.Value < utcNow;
                overdue |= kind == MonitoringQueueKind.Assistance &&
                    (candidate.Status is nameof(AssistanceStatus.Requested) or nameof(AssistanceStatus.Accepted) or nameof(AssistanceStatus.InProgress)) &&
                    candidate.DueAtUtc.HasValue && candidate.DueAtUtc.Value < utcNow;
                items.Add(new MonitoringQueueItem(
                    candidate.CaseId,
                    kind,
                    candidate.WorkItemId,
                    candidate.Summary,
                    candidate.AssigneeActorId,
                    candidate.DueAtUtc,
                    candidate.Status,
                    overdue,
                    candidate.RecordedAtUtc,
                    candidate.AggregateVersion));
            }
        }

        return new MonitoringQueueResult(CaseQueueAccess.Allowed, items);
    }

    private static int Take(int? limit)
    {
        var take = limit ?? DefaultLimit;
        if (take < 1 || take > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The queue limit must be between 1 and 500.");
        }

        return take;
    }
}
