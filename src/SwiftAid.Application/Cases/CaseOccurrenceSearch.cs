using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

/// <summary>Finds searchable, readable cases using recorded occurrence places, never access-region links.</summary>
public sealed class CaseOccurrenceSearch(ICaseQueueQuery queue, ICaseOccurrenceLocationQuery locations)
{
    public const int PageSize = 50;

    public async Task<CaseOccurrenceSearchResult> SearchAsync(ActorContext actor, string country, string placeId,
        int limit, CancellationToken ct, DateTimeOffset? reportedFromUtc = null, DateTimeOffset? reportedBeforeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (country is not { Length: 2 } || country.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("An uppercase country code is required.", nameof(country));
        if (string.IsNullOrWhiteSpace(placeId) || placeId.Length > 120 || placeId != placeId.Trim())
            throw new ArgumentException("A bounded exact place identifier is required.", nameof(placeId));
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));

        var matches = new List<CaseOccurrenceMatch>();
        var seenCases = new HashSet<Guid>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var unknown = false;
        var incomplete = false;
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
                    return new(CaseQueueAccess.Unavailable, [], false, false);
                foreach (var item in page.Items)
                {
                    if (!item.HasReportJournal) { unknown = true; continue; }
                    var location = await locations.GetLocationAsync(actor, item.CaseId, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (location.Access == CaseOccurrenceLocationAccess.Unavailable)
                        return matches.Count == 0
                            ? new(CaseQueueAccess.Unavailable, [], false, false)
                            : new(CaseQueueAccess.Allowed, matches, true, unknown, LocationReadUnavailable: true);
                    if (location.Access != CaseOccurrenceLocationAccess.Allowed)
                    {
                        // Access or existence can change between the queue and report reads.
                        // Omit the record without presenting the scan as complete.
                        incomplete = true;
                        continue;
                    }
                    if (location.Location is null) { unknown = true; continue; }
                    var place = location.Location;
                    if (place.CountryCode != country) { unknown = true; continue; }
                    if (place.RegionId is null && place.AreaId is null && place.LocalityId is null)
                    { unknown = true; continue; }
                    if (placeId != place.RegionId && placeId != place.AreaId && placeId != place.LocalityId)
                    {
                        if (place.RegionId is null || place.AreaId is null || place.LocalityId is null) unknown = true;
                        continue;
                    }
                    if (location.ReportRevision is null) return new(CaseQueueAccess.Unavailable, [], false, false);
                    matches.Add(new(item, location.ReportRevision.Value));
                    if (matches.Count > limit) return new(CaseQueueAccess.Allowed, matches.Take(limit).ToArray(), true, unknown);
                }
                cursor = page.NextCursor;
                if (cursor is not null && (page.Items.Count == 0 || !seenCursors.Add(cursor)))
                    return new(CaseQueueAccess.Unavailable, [], false, false);
            } while (cursor is not null);
            return new(CaseQueueAccess.Allowed, matches, cursor is not null || incomplete, unknown);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(CaseQueueAccess.Unavailable, [], false, false); }
    }
}

public sealed record CaseOccurrenceMatch(CaseQueueItem Case, long ReportRevision);
public sealed record CaseOccurrenceSearchResult(CaseQueueAccess Access, IReadOnlyList<CaseOccurrenceMatch> Items,
    bool Truncated, bool UnknownLocations, bool LocationReadUnavailable = false);
