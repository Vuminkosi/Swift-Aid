using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>One officer-owned managed case with the fields needed for dashboard aggregation.</summary>
public sealed record DashboardAggregateCaseRow(
    Guid CaseId,
    Sector Sector,
    ClinicalClassification Classification,
    CaseWorkflow Workflow,
    string CountryCode,
    IReadOnlyList<string> RegionIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Persistence port for the officer-scope dashboard aggregate.</summary>
public interface IDashboardAggregateStore
{
    Task<IReadOnlyList<DashboardAggregateCaseRow>> GetOwnedCaseRowsAsync(
        string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct);
}

/// <summary>Access outcome for a scoped dashboard aggregate read.</summary>
public enum DashboardAggregateAccess
{
    Allowed,
    Denied,
    Unavailable
}

/// <summary>
/// Authorized aggregate read outcome. An empty row list is a normal allowed
/// result: it never reveals whether other cases exist or who may read them.
/// </summary>
public sealed record DashboardAggregateResult(
    DashboardAggregateAccess Access,
    IReadOnlyList<DashboardAggregateCaseRow> Rows,
    DateTimeOffset AsOfUtc,
    bool Truncated);

public interface IDashboardAggregateQuery
{
    Task<DashboardAggregateResult> GetAsync(
        ActorContext actor,
        TimeSpan window,
        CancellationToken ct);
}

/// <summary>
/// Dashboard aggregates over managed cases owned by the calling officer that the
/// caller may still read. Per-case <c>ReadCase</c> enforcement runs inside one
/// read-mode lease per returned case; denied, missing, and vanished cases are
/// skipped and never listed — the same rule as the officer queue. Only routing
/// identity, sector, classification, workflow, region links, and record times
/// are read: no sealed payloads, narratives, or coordinates.
/// </summary>
public sealed class DashboardAggregateQuery(
    IDashboardAggregateStore store,
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IEllaiCaseAuthScopeFactory? scopeFactory = null) : IDashboardAggregateQuery
{
    public const int MaxCases = 500;

    /// <summary>Maximum concurrent authorization lanes. Each lane owns an
    /// isolated scope, so lanes never share a DbContext, lease, or policy.</summary>
    public const int MaxAuthorizationParallelism = 8;

    private static readonly TimeSpan MinimumWindow = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(365);

    private readonly IDashboardAggregateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ICaseAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ICaseCommandUnitOfWork _unitOfWork =
        unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<DashboardAggregateResult> GetAsync(
        ActorContext actor,
        TimeSpan window,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (window < MinimumWindow || window > MaximumWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The aggregate window must be between 1 and 365 days.");
        }

        var now = _timeProvider.GetUtcNow();
        var sinceUtc = now.Add(-window);

        IReadOnlyList<DashboardAggregateCaseRow> candidates;
        try
        {
            candidates = await _store.GetOwnedCaseRowsAsync(actor.ActorId, sinceUtc, MaxCases + 1, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new DashboardAggregateResult(DashboardAggregateAccess.Unavailable, [], now, false);
        }

        var truncated = candidates.Count > MaxCases;
        var shortlisted = candidates.Take(MaxCases).ToList();
        var readable = scopeFactory is null
            ? await AuthorizeSequentialAsync(shortlisted, actor, _unitOfWork, _policy, ct).ConfigureAwait(false)
            : await AuthorizeParallelAsync(shortlisted, actor, scopeFactory, ct).ConfigureAwait(false);

        return new DashboardAggregateResult(DashboardAggregateAccess.Allowed, readable, now, truncated);
    }

    private static async Task<IReadOnlyList<DashboardAggregateCaseRow>> AuthorizeSequentialAsync(
        IReadOnlyList<DashboardAggregateCaseRow> shortlisted,
        ActorContext actor,
        ICaseCommandUnitOfWork unitOfWork,
        ICaseAccessPolicy policy,
        CancellationToken ct)
    {
        var readable = new List<DashboardAggregateCaseRow>();
        foreach (var candidate in shortlisted)
        {
            var authorized = await AuthorizeOneAsync(unitOfWork, policy, actor, candidate, ct).ConfigureAwait(false);
            if (authorized is not null)
            {
                readable.Add(authorized);
            }
        }

        return readable;
    }

    private static async Task<IReadOnlyList<DashboardAggregateCaseRow>> AuthorizeParallelAsync(
        IReadOnlyList<DashboardAggregateCaseRow> shortlisted,
        ActorContext actor,
        IEllaiCaseAuthScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var readable = new System.Collections.Concurrent.ConcurrentBag<DashboardAggregateCaseRow>();
        var chunks = Partition(shortlisted, MaxAuthorizationParallelism);
        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = MaxAuthorizationParallelism, CancellationToken = ct },
            async (chunk, token) =>
            {
                await using var scope = scopeFactory.CreateScope();
                foreach (var candidate in chunk)
                {
                    var authorized = await AuthorizeOneAsync(
                        scope.UnitOfWork, scope.Policy, actor, candidate, token).ConfigureAwait(false);
                    if (authorized is not null)
                    {
                        readable.Add(authorized);
                    }
                }
            }).ConfigureAwait(false);
        return readable.ToList();
    }

    private static IReadOnlyList<IReadOnlyList<DashboardAggregateCaseRow>> Partition(
        IReadOnlyList<DashboardAggregateCaseRow> shortlisted, int maxChunks)
    {
        if (shortlisted.Count == 0)
        {
            return Array.Empty<IReadOnlyList<DashboardAggregateCaseRow>>();
        }

        var chunkCount = Math.Min(maxChunks, shortlisted.Count);
        var chunkSize = (shortlisted.Count + chunkCount - 1) / chunkCount;
        var chunks = new List<IReadOnlyList<DashboardAggregateCaseRow>>(chunkCount);
        for (var i = 0; i < shortlisted.Count; i += chunkSize)
        {
            chunks.Add(shortlisted.Skip(i).Take(chunkSize).ToList());
        }

        return chunks;
    }

    private static async Task<DashboardAggregateCaseRow?> AuthorizeOneAsync(
        ICaseCommandUnitOfWork unitOfWork,
        ICaseAccessPolicy policy,
        ActorContext actor,
        DashboardAggregateCaseRow candidate,
        CancellationToken ct)
    {
        if (candidate.CaseId == Guid.Empty || string.IsNullOrWhiteSpace(candidate.CountryCode))
        {
            return null;
        }

        ICaseCommandLease lease;
        try
        {
            lease = await unitOfWork.BeginCaseAsync(
                actor, candidate.CaseId, System.Collections.Immutable.ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        await using (lease.ConfigureAwait(false))
        {
            var decision = await policy.AuthorizeAsync(actor, candidate.CaseId, CaseOperation.ReadCase, ct)
                .ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return null;
            }

            await lease.CommitAsync(ct).ConfigureAwait(false);
            return candidate;
        }
    }
}
