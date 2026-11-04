using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>Groups currently searchable and readable reports by a recorded location dimension.</summary>
public sealed class CaseOccurrenceGrouping(ICaseQueueQuery queue, ICaseOccurrenceLocationQuery locations)
{
    public const int MaximumCandidates = 80;
    public const int PageSize = 40;

    public async Task<CaseOccurrenceGroupingResult> GroupByRegionAsync(ActorContext actor, string country,
        int limit, DateTimeOffset? reportedFromUtc, DateTimeOffset? reportedBeforeUtc, CancellationToken ct) =>
        await GroupAsync(actor, country, "region", limit, reportedFromUtc, reportedBeforeUtc, ct).ConfigureAwait(false);

    public async Task<CaseOccurrenceGroupingResult> GroupAsync(ActorContext actor, string country, string dimension,
        int limit, DateTimeOffset? reportedFromUtc, DateTimeOffset? reportedBeforeUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (country is not { Length: 2 } || country.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("An uppercase country code is required.", nameof(country));
        if (dimension is not ("region" or "locality")) throw new ArgumentOutOfRangeException(nameof(dimension));
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));

        var groups = new Dictionary<string, MutableOccurrenceGroup>(StringComparer.OrdinalIgnoreCase);
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

                    var (labelValue, idValue) = dimension == "region"
                        ? (place.Region, place.RegionId)
                        : (place.Locality, place.LocalityId);
                    var label = labelValue?.Trim();
                    var locationId = idValue?.Trim();
                    if (string.IsNullOrWhiteSpace(locationId) && string.IsNullOrWhiteSpace(label))
                    {
                        unknownLocations = true;
                        continue;
                    }

                    var key = country + ":" + (string.IsNullOrWhiteSpace(locationId)
                        ? "reported:" + NormalizeLabel(label!)
                        : "registry:" + locationId);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new MutableOccurrenceGroup(locationId, label is { Length: > 0 } ? label : locationId!);
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
                .Select(group => new CaseOccurrenceRegionGroup(group.LocationId, group.Label, group.Count,
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

    private sealed class MutableOccurrenceGroup(string? locationId, string label)
    {
        public string? LocationId { get; } = locationId;
        public string Label { get; } = label;
        public int Count { get; set; }
        public List<Guid> CaseIds { get; } = [];
    }
}

public sealed record CaseOccurrenceRegionGroup(string? RegionId, string Label, int Count, IReadOnlyList<Guid> CaseIds)
{
    public string? LocationId => RegionId;
}

public sealed record CaseOccurrenceGroupingResult(CaseQueueAccess Access,
    IReadOnlyList<CaseOccurrenceRegionGroup> Groups, IReadOnlyList<CaseQueueItem> Cases,
    int ScannedCandidates, bool Truncated, bool UnknownLocations);
