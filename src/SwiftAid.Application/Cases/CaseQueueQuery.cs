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

/// <summary>Keyset position used to resume after a previously authorized row; it is not an access grant.</summary>
public sealed record CaseQueueContinuation(DateTimeOffset SortTimestampUtc, Guid CaseId);

/// <summary>Recorded routing filters, not clinical or classification-review inference.</summary>
public sealed record CaseQueueFilter(string? CountryCode = null, string? Workflow = null, bool NewestReportedFirst = false,
    IReadOnlyList<Guid>? CaseIds = null, DateTimeOffset? ReportedFromUtc = null, DateTimeOffset? ReportedBeforeUtc = null);

/// <summary>Persistence port for owner queue and grant-scoped case search. Rows arrive newest-first.</summary>
public interface ICaseQueueStore
{
    Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(
        string ownerActorId, int take, CancellationToken ct);

    Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(
        string ownerActorId, int take, CaseQueueFilter filter, CancellationToken ct) =>
        throw new NotSupportedException("Filtered case queries are unavailable.");

    Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAfterAsync(
        string ownerActorId, int take, CaseQueueFilter? filter, CaseQueueContinuation? after, CancellationToken ct)
    {
        if (after is not null)
            throw new NotSupportedException("Case queue keyset paging is unavailable.");
        return filter is null
            ? GetOwnedCasesAsync(ownerActorId, take, ct)
            : GetOwnedCasesAsync(ownerActorId, take, filter, ct);
    }

    Task<IReadOnlyList<CaseQueueCandidate>> GetSearchableCasesAfterAsync(
        string actorId, long accessEpoch, int take, CaseQueueFilter? filter, CaseQueueContinuation? after,
        CancellationToken ct) =>
        throw new NotSupportedException("Searchable case queries are unavailable.");
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

    Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, string? cursor, CancellationToken ct) =>
        cursor is null
            ? GetMineAsync(actor, limit, ct)
            : Task.FromResult(new CaseQueueResult(CaseQueueAccess.Unavailable, []));

    Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter,
        CancellationToken ct) => throw new NotSupportedException("Filtered case queries are unavailable.");

    Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter,
        string? cursor, CancellationToken ct) => cursor is null
            ? GetMineAsync(actor, limit, filter, ct)
            : Task.FromResult(new CaseQueueResult(CaseQueueAccess.Unavailable, []));

    Task<CaseQueueResult> GetSearchableAsync(ActorContext actor, int? limit, CaseQueueFilter? filter,
        string? cursor, CancellationToken ct) =>
        Task.FromResult(new CaseQueueResult(CaseQueueAccess.Unavailable, []));
}

/// <summary>
/// Authorized case queues. Owner listing remains scoped to the calling officer;
/// searchable listing uses grant-bounded candidates and requires both <c>Search</c>
/// and <c>ReadCase</c>. Both paths authorize each case through the shared resource
/// policy inside one read-mode lease. Denied, missing, and vanished cases are
/// skipped and never listed.
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
        ReadAsync(actor, limit, null, null, false, ct);

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, string? cursor, CancellationToken ct) =>
        ReadAsync(actor, limit, null, cursor, false, ct);

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter,
        string? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        return ReadAsync(actor, limit, filter, cursor, false, ct);
    }

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        return ReadAsync(actor, limit, filter, null, false, ct);
    }

    public Task<CaseQueueResult> GetSearchableAsync(ActorContext actor, int? limit, CaseQueueFilter? filter,
        string? cursor, CancellationToken ct)
    {
        if (filter is not null) ValidateFilter(filter);
        return ReadAsync(actor, limit, filter, cursor, true, ct);
    }

    private static void ValidateFilter(CaseQueueFilter filter)
    {
        if (filter.CountryCode is { } country && (country.Length != 2 || country.Any(c => c is < 'A' or > 'Z')))
            throw new ArgumentException("Country must be an uppercase two-letter code.", nameof(filter));
        if (filter.Workflow is { } workflow && workflow is not ("Reported" or "UnderReview" or "Active" or "AwaitingFollowUp" or "Resolved"))
            throw new ArgumentException("Unknown recorded workflow.", nameof(filter));
        if (filter.CaseIds is { } ids && (ids.Count > 20 || ids.Any(id => id == Guid.Empty)))
            throw new ArgumentException("At most 20 nonempty case IDs are supported.", nameof(filter));
        if ((filter.ReportedFromUtc is null) != (filter.ReportedBeforeUtc is null) ||
            filter.ReportedFromUtc is { } from && filter.ReportedBeforeUtc is { } before &&
            (from.Offset != TimeSpan.Zero || before.Offset != TimeSpan.Zero || before <= from || before - from > TimeSpan.FromDays(365)))
            throw new ArgumentException("Reported-time filters require a bounded UTC range of at most 365 days.", nameof(filter));
    }

    private async Task<CaseQueueResult> ReadAsync(ActorContext actor, int? limit, CaseQueueFilter? filter,
        string? cursor, bool requireSearch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var take = limit ?? DefaultLimit;
        if (take < 1 || take > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The queue limit must be between 1 and 500.");
        }

        var continuation = DecodeCursor(cursor);

        const int candidateBatchSize = 200;
        IReadOnlyList<CaseQueueCandidate> candidates;
        var items = new List<CaseQueueItem>(take);
        CaseQueueCandidate? lastReturned = null;
        try
        {
            while (true)
            {
                candidates = requireSearch
                    ? await _store.GetSearchableCasesAfterAsync(actor.ActorId, actor.AccessEpoch,
                        candidateBatchSize, filter, continuation, ct).ConfigureAwait(false)
                    : await _store.GetOwnedCasesAfterAsync(
                        actor.ActorId, candidateBatchSize, filter, continuation, ct).ConfigureAwait(false);
                foreach (var candidate in candidates)
                {
                    continuation = new CaseQueueContinuation(GetSortTimestamp(candidate, filter), candidate.CaseId);
                    if (candidate.CaseId == Guid.Empty) continue;

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
                        var decision = requireSearch
                            ? await _policy.AuthorizeAllAsync(actor, candidate.CaseId,
                                [CaseOperation.Search, CaseOperation.ReadCase], ct).ConfigureAwait(false)
                            : await _policy.AuthorizeAsync(actor, candidate.CaseId, CaseOperation.ReadCase, ct).ConfigureAwait(false);
                        if (!decision.IsAllowed) continue;

                        await lease.CommitAsync(ct).ConfigureAwait(false);
                        if (items.Count == take)
                            return new CaseQueueResult(CaseQueueAccess.Allowed, items, EncodeCursor(lastReturned!, filter));

                        items.Add(new CaseQueueItem(
                            candidate.CaseId, candidate.Workflow, candidate.CountryCode,
                            candidate.AccountableOwnerActorId, candidate.AggregateVersion,
                            candidate.UpdatedAtUtc, candidate.Sector, candidate.Classification,
                            candidate.WorkspaceId, candidate.CreatedAtUtc));
                        lastReturned = candidate;
                    }
                }

                if (candidates.Count < candidateBatchSize)
                    return new CaseQueueResult(CaseQueueAccess.Allowed, items);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }

    private static string EncodeCursor(CaseQueueCandidate candidate, CaseQueueFilter? filter) =>
        $"{GetSortTimestamp(candidate, filter).UtcDateTime.Ticks:x}.{candidate.CaseId:N}";

    private static DateTimeOffset GetSortTimestamp(CaseQueueCandidate candidate, CaseQueueFilter? filter) =>
        filter?.NewestReportedFirst == true
            ? candidate.CreatedAtUtc ?? candidate.LastRecordedAtUtc
            : candidate.LastRecordedAtUtc;

    private static CaseQueueContinuation? DecodeCursor(string? value)
    {
        if (value is null) return null;
        var separator = value.IndexOf('.');
        if (value.Length is < 34 or > 49 || separator is < 1 or > 16 ||
            !long.TryParse(value.AsSpan(0, separator), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var ticks) ||
            ticks < 0 || ticks > DateTime.MaxValue.Ticks ||
            !Guid.TryParseExact(value.AsSpan(separator + 1), "N", out var caseId) || caseId == Guid.Empty)
            throw new ArgumentException("The case queue cursor is invalid.", nameof(value));
        return new CaseQueueContinuation(new DateTimeOffset(ticks, TimeSpan.Zero), caseId);
    }
}
