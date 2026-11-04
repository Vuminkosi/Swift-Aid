using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>Groups currently searchable and readable reports by their recorded region.</summary>
public sealed class CaseOccurrenceGrouping(ICaseQueueQuery queue, ICaseOccurrenceLocationQuery locations)
{
    public const int MaximumCandidates = 200;
    public const int PageSize = 50;

    public async Task<CaseOccurrenceGroupingResult> GroupByRegionAsync(ActorContext actor, string country,
        int limit, DateTimeOffset? reportedFromUtc, DateTimeOffset? reportedBeforeUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (country is not { Length: 2 } || country.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("An uppercase country code is required.", nameof(country));
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));

        var groups = new Dictionary<string, MutableRegionGroup>(StringComparer.OrdinalIgnoreCase);
        var cases = new List<CaseQueueItem>();
        var seenCases = new HashSet<Guid>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var unknownLocations = false;
        var incomplete = false;
        var scanned = 0;
        string? cursor = null;
        var filter = new CaseQueueFilter(country, NewestReportedFirst: true,
            ReportedFromUtc: reportedFromUtc, ReportedBeforeUtc: reportedBeforeUtc);

        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var page = await queue.GetSearchableAsync(actor, PageSize, filter, cursor, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (page.Access != CaseQueueAccess.Allowed || page.Items.Count > PageSize ||
                    page.Items.Any(item => item.CaseId == Guid.Empty || !seenCases.Add(item.CaseId)))
                    return Unavailable();

                foreach (var item in page.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    var location = await locations.GetLocationAsync(actor, item.CaseId, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (location.Access == CaseOccurrenceLocationAccess.Unavailable)
                        return Unavailable();
                    if (location.Access != CaseOccurrenceLocationAccess.Allowed)
                    {
                        incomplete = true;
                        continue;
                    }
                    if (location.Location is not { } place || place.CountryCode != country)
                    {
                        unknownLocations = true;
                        continue;
                    }

                    var regionLabel = place.Region?.Trim();
                    var regionId = place.RegionId?.Trim();
                    if (string.IsNullOrWhiteSpace(regionId) && string.IsNullOrWhiteSpace(regionLabel))
                    {
                        unknownLocations = true;
                        continue;
                    }

                    var key = string.IsNullOrWhiteSpace(regionId)
                        ? "reported:" + NormalizeLabel(regionLabel!)
                        : "registry:" + regionId;
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new MutableRegionGroup(regionId, regionLabel is { Length: > 0 } ? regionLabel : regionId!);
                        groups.Add(key, group);
                    }
                    group.CaseIds.Add(item.CaseId);
                    group.Count++;
                    cases.Add(item);
                }

                scanned += page.Items.Count;
                cursor = page.NextCursor;
                if (cursor is not null && (page.Items.Count == 0 || !seenCursors.Add(cursor)))
                    return Unavailable();
            } while (cursor is not null && scanned < MaximumCandidates);

            var ordered = groups.Values
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Label, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(group => new CaseOccurrenceRegionGroup(group.RegionId, group.Label, group.Count,
                    group.CaseIds.Order().ToArray()))
                .ToArray();
            var truncated = cursor is not null || incomplete || unknownLocations || groups.Count > limit;
            return new(CaseQueueAccess.Allowed, ordered, cases, scanned, truncated, unknownLocations);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Unavailable();
        }

        static CaseOccurrenceGroupingResult Unavailable() =>
            new(CaseQueueAccess.Unavailable, [], [], 0, false, false);
    }

    private static string NormalizeLabel(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private sealed class MutableRegionGroup(string? regionId, string label)
    {
        public string? RegionId { get; } = regionId;
        public string Label { get; } = label;
        public int Count { get; set; }
        public List<Guid> CaseIds { get; } = [];
    }
}

public sealed record CaseOccurrenceRegionGroup(string? RegionId, string Label, int Count, IReadOnlyList<Guid> CaseIds);

public sealed record CaseOccurrenceGroupingResult(CaseQueueAccess Access,
    IReadOnlyList<CaseOccurrenceRegionGroup> Groups, IReadOnlyList<CaseQueueItem> Cases,
    int ScannedCandidates, bool Truncated, bool UnknownLocations);
