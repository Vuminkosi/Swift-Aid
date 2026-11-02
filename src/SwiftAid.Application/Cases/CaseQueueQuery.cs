using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>One officer-owned candidate with its most recently recorded journal time.</summary>
public sealed record CaseQueueCandidate(
    Guid CaseId,
    string Workflow,
    string CountryCode,
    string AccountableOwnerActorId,
    long AggregateVersion,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset LastRecordedAtUtc,
    string? Sector = null,
    string? Classification = null,
    string? WorkspaceId = null,
    DateTimeOffset? CreatedAtUtc = null);

/// <summary>Recorded routing filters, not clinical or classification-review inference.</summary>
public sealed record CaseQueueFilter(string? CountryCode = null, string? Workflow = null, bool NewestReportedFirst = false, IReadOnlyList<Guid>? CaseIds = null);

/// <summary>Persistence port for the "my cases" queue. Rows arrive newest-first.</summary>
public interface ICaseQueueStore
{
    Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(
        string ownerActorId, int take, CancellationToken ct);

    Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(
        string ownerActorId, int take, CaseQueueFilter filter, CancellationToken ct) =>
        throw new NotSupportedException("Filtered case queries are unavailable.");
}

/// <summary>Bounded map candidates; callers must authorize each report before using its evidence.</summary>
public interface ICaseMapCandidateStore
{
    Task<IReadOnlyList<CaseQueueCandidate>> GetAsync(string ownerActorId, string workspaceId,
        string countryCode, DateTimeOffset sinceUtc, int take, CancellationToken ct);
}

public interface ICaseQueueQuery
{
    Task<CaseQueueResult> GetMineAsync(
        ActorContext actor,
        int? limit,
        CancellationToken ct);

    Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter,
        CancellationToken ct) => throw new NotSupportedException("Filtered case queries are unavailable.");
}

/// <summary>
/// Authorized "my cases" queue. Lists cases owned by the calling officer ordered by
/// the most recently recorded journal event, then enforces <c>ReadCase</c> through
/// the shared case resource policy inside one read-mode lease per returned case.
/// Denied, missing, and vanished cases are skipped and never listed. Like the
/// timeline, an empty identifier or a case the officer may not read yields no row.
/// </summary>
public sealed class CaseQueueQuery(
    ICaseQueueStore store,
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork) : ICaseQueueQuery
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    private readonly ICaseQueueStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ICaseAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ICaseCommandUnitOfWork _unitOfWork =
        unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) =>
        ReadAsync(actor, limit, null, ct);

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.CountryCode is { } country && (country.Length != 2 || country.Any(c => c is < 'A' or > 'Z')))
            throw new ArgumentException("Country must be an uppercase two-letter code.", nameof(filter));
        if (filter.Workflow is { } workflow && workflow is not ("Reported" or "UnderReview" or "Active" or "AwaitingFollowUp" or "Resolved"))
            throw new ArgumentException("Unknown recorded workflow.", nameof(filter));
        if (filter.CaseIds is { } ids && (ids.Count > 20 || ids.Any(id => id == Guid.Empty)))
            throw new ArgumentException("At most 20 nonempty case IDs are supported.", nameof(filter));
        return ReadAsync(actor, limit, filter, ct);
    }

    private async Task<CaseQueueResult> ReadAsync(ActorContext actor, int? limit, CaseQueueFilter? filter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var take = limit ?? DefaultLimit;
        if (take < 1 || take > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The queue limit must be between 1 and 500.");
        }

        IReadOnlyList<CaseQueueCandidate> candidates;
        try
        {
            candidates = filter is null
                ? await _store.GetOwnedCasesAsync(actor.ActorId, take, ct).ConfigureAwait(false)
                : await _store.GetOwnedCasesAsync(actor.ActorId, take, filter, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }

        var items = new List<CaseQueueItem>();
        foreach (var candidate in candidates)
        {
            if (candidate.CaseId == Guid.Empty)
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
                items.Add(new CaseQueueItem(
                    candidate.CaseId,
                    candidate.Workflow,
                    candidate.CountryCode,
                    candidate.AccountableOwnerActorId,
                    candidate.AggregateVersion,
                    candidate.UpdatedAtUtc,
                    candidate.Sector,
                    candidate.Classification, candidate.WorkspaceId, candidate.CreatedAtUtc));
            }
        }

        return new CaseQueueResult(CaseQueueAccess.Allowed, items);
    }
}
