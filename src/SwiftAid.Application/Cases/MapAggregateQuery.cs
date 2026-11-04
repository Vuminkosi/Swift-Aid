using System.Collections.Concurrent;
using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Application.Cases;

/// <summary>One officer-owned managed case with the fields needed for area aggregation.</summary>
public sealed record MapAggregateCaseRow(
    Guid CaseId,
    string CountryCode,
    IReadOnlyList<string> RegionIds,
    Sector Sector,
    DateTimeOffset CreatedAtUtc);

/// <summary>Persistence port for the officer-scope surveillance aggregate.</summary>
public interface IMapAggregateStore
{
    Task<IReadOnlyList<MapAggregateCaseRow>> GetOwnedCaseAreasAsync(
        string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct);
}

/// <summary>One isolated authorization lane: its own unit of work and
/// policy so parallel lanes never share a DbContext or lease.</summary>
public interface IEllaiCaseAuthScope : IAsyncDisposable
{
    ICaseCommandUnitOfWork UnitOfWork { get; }

    ICaseAccessPolicy Policy { get; }
}

/// <summary>Creates isolated authorization lanes for bounded parallel
/// per-case checks. A null factory selects the sequential path.</summary>
public interface IEllaiCaseAuthScopeFactory
{
    IEllaiCaseAuthScope CreateScope();
}

public interface IMapAggregateQuery
{
    Task<SurveillanceAggregateResult> GetAsync(
        ActorContext actor,
        string? regionCode,
        TimeSpan window,
        CancellationToken ct);
}

/// <summary>
/// Area aggregates over managed cases owned by the calling officer that the
/// caller may still read. Per-case <c>ReadCase</c> enforcement runs inside one
/// read-mode lease per returned case; denied, missing, and vanished cases are
/// skipped and never listed — the same rule as the officer queue. Groups with
/// fewer than <see cref="SuppressionThreshold"/> distinct cases are suppressed:
/// small counts are withheld to protect privacy. Country labels come from the
/// public intake country catalogue; region rows carry their registry
/// identifier as the label. The window applies to record creation time, which
/// is the only event timestamp on the managed case record.
/// </summary>
public sealed class SurveillanceMapAggregateQuery(
    IMapAggregateStore store,
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork,
    IIntakePublicMetadataProvider metadataProvider,
    TimeProvider timeProvider,
    IEllaiCaseAuthScopeFactory? scopeFactory = null) : IMapAggregateQuery
{
    public const int MaxCases = 500;

    /// <summary>Maximum concurrent authorization lanes. Each lane owns an
    /// isolated scope, so lanes never share a DbContext, lease, or policy.</summary>
    public const int MaxAuthorizationParallelism = 8;

    /// <summary>Minimum distinct cases per row before counts may be shown.</summary>
    public const int SuppressionThreshold = 5;

    private static readonly TimeSpan MinimumWindow = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(365);

    private readonly IMapAggregateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ICaseAccessPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ICaseCommandUnitOfWork _unitOfWork =
        unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly IIntakePublicMetadataProvider _metadataProvider =
        metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<SurveillanceAggregateResult> GetAsync(
        ActorContext actor,
        string? regionCode,
        TimeSpan window,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!IsRegionCode(regionCode))
        {
            throw new ArgumentException("The region code is invalid.", nameof(regionCode));
        }

        if (window < MinimumWindow || window > MaximumWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The aggregate window must be between 1 and 365 days.");
        }

        var now = _timeProvider.GetUtcNow();
        var sinceUtc = now.Add(-window);

        IReadOnlyList<MapAggregateCaseRow> candidates;
        try
        {
            candidates = await _store.GetOwnedCaseAreasAsync(actor.ActorId, sinceUtc, MaxCases + 1, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new SurveillanceAggregateResult(SurveillanceAggregateAccess.Unavailable, [], now, false);
        }

        var truncated = candidates.Count > MaxCases;
        var shortlisted = candidates.Take(MaxCases).ToList();
        var readable = scopeFactory is null
            ? await AuthorizeSequentialAsync(shortlisted, actor, _unitOfWork, _policy, ct).ConfigureAwait(false)
            : await AuthorizeParallelAsync(shortlisted, actor, scopeFactory, ct).ConfigureAwait(false);

        var labels = await LoadCountryLabelsAsync(ct).ConfigureAwait(false);
        var areas = Aggregate(readable, regionCode, labels, now);
        return new SurveillanceAggregateResult(SurveillanceAggregateAccess.Allowed, areas, now, truncated);
    }

    private static async Task<IReadOnlyList<MapAggregateCaseRow>> AuthorizeSequentialAsync(
        IReadOnlyList<MapAggregateCaseRow> shortlisted,
        ActorContext actor,
        ICaseCommandUnitOfWork unitOfWork,
        ICaseAccessPolicy policy,
        CancellationToken ct)
    {
        var readable = new List<MapAggregateCaseRow>();
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

    private static async Task<IReadOnlyList<MapAggregateCaseRow>> AuthorizeParallelAsync(
        IReadOnlyList<MapAggregateCaseRow> shortlisted,
        ActorContext actor,
        IEllaiCaseAuthScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var readable = new ConcurrentBag<MapAggregateCaseRow>();
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

    private static IReadOnlyList<IReadOnlyList<MapAggregateCaseRow>> Partition(
        IReadOnlyList<MapAggregateCaseRow> shortlisted, int maxChunks)
    {
        if (shortlisted.Count == 0)
        {
            return Array.Empty<IReadOnlyList<MapAggregateCaseRow>>();
        }

        var chunkCount = Math.Min(maxChunks, shortlisted.Count);
        var chunkSize = (shortlisted.Count + chunkCount - 1) / chunkCount;
        var chunks = new List<IReadOnlyList<MapAggregateCaseRow>>(chunkCount);
        for (var i = 0; i < shortlisted.Count; i += chunkSize)
        {
            chunks.Add(shortlisted.Skip(i).Take(chunkSize).ToList());
        }

        return chunks;
    }

    private static async Task<MapAggregateCaseRow?> AuthorizeOneAsync(
        ICaseCommandUnitOfWork unitOfWork,
        ICaseAccessPolicy policy,
        ActorContext actor,
        MapAggregateCaseRow candidate,
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
                actor, candidate.CaseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct).ConfigureAwait(false);
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

    private static IReadOnlyList<SurveillanceAggregateArea> Aggregate(
        IReadOnlyList<MapAggregateCaseRow> rows,
        string? regionCode,
        IReadOnlyDictionary<string, string> countryLabels,
        DateTimeOffset asOfUtc)
    {
        var filter = string.IsNullOrWhiteSpace(regionCode) ? null : regionCode;
        var inScope = filter is null
            ? rows
            : rows.Where(row =>
                string.Equals(row.CountryCode, filter, StringComparison.OrdinalIgnoreCase) ||
                row.RegionIds.Any(region =>
                    string.Equals(region, filter, StringComparison.OrdinalIgnoreCase))).ToList();

        var areas = new List<SurveillanceAggregateArea>();
        foreach (var country in inScope.GroupBy(row => row.CountryCode, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            areas.Add(BuildArea(
                country.Key,
                countryLabels.GetValueOrDefault(country.Key) ?? country.Key,
                SurveillanceAggregatePrecision.Country,
                country,
                asOfUtc));

            foreach (var region in country
                         .SelectMany(row => row.RegionIds.Select(regionId => (Row: row, RegionId: regionId)))
                         .GroupBy(pair => pair.RegionId, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (filter is not null &&
                    !string.Equals(region.Key, filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                areas.Add(BuildArea(
                    region.Key,
                    country.Key + " / " + region.Key,
                    SurveillanceAggregatePrecision.Region,
                    region.Select(pair => pair.Row),
                    asOfUtc));
            }
        }

        return areas;
    }

    private static SurveillanceAggregateArea BuildArea(
        string areaCode,
        string areaLabel,
        SurveillanceAggregatePrecision precision,
        IEnumerable<MapAggregateCaseRow> rows,
        DateTimeOffset asOfUtc)
    {
        var distinct = rows.Select(row => row.CaseId).Distinct().ToArray();
        if (distinct.Length < SuppressionThreshold)
        {
            return new SurveillanceAggregateArea(
                areaCode,
                areaLabel,
                precision,
                null,
                null,
                null,
                true,
                "Small counts are withheld to protect privacy.",
                asOfUtc);
        }

        var byCase = rows
            .GroupBy(row => row.CaseId)
            .Select(group => group.First())
            .ToArray();
        var human = byCase.Count(row => row.Sector == Sector.HumanHealth);
        var animal = byCase.Count(row => row.Sector == Sector.AnimalHealth);
        return new SurveillanceAggregateArea(
            areaCode,
            areaLabel,
            precision,
            human,
            animal,
            byCase.Length,
            false,
            null,
            asOfUtc);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadCountryLabelsAsync(CancellationToken ct)
    {
        try
        {
            var catalogue = await _metadataProvider.GetCountriesAsync(ct).ConfigureAwait(false);
            return catalogue.Countries.ToDictionary(
                country => country.Code, country => country.Label, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool IsRegionCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        (value.Length is >= 2 and <= 24 && value.All(IsRegionCodeCharacter));

    private static bool IsRegionCodeCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '-';
}
