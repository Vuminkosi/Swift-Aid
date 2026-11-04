using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

public sealed record CaseReportingFrequency(string Label, int Cases);

public sealed record CaseReportingSummary(
    int Total, int Classified, int Unclassified, int UnknownClassification,
    int UnknownLocations, int MissingReportJournals, IReadOnlyList<CaseReportingFrequency> Categories,
    IReadOnlyList<CaseReportingFrequency> Observations);

/// <summary>
/// Complete counts over the searchable, readable queue. Never turns a bounded
/// page into a population total or uses access regions as occurrence locations.
/// </summary>
public sealed class CaseReportingSummaryQuery(ICaseQueueQuery queue, ICaseReportReadQuery reports)
{
    public async Task<CaseReportingSummary> ReadAsync(ActorContext actor, string? country,
        string? placeId, DateTimeOffset? from, DateTimeOffset? before, bool includeObservations,
        CancellationToken ct, string? placeName = null)
    {
        var seen = new HashSet<Guid>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var observations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        var classified = 0;
        var unclassified = 0;
        var unknownClassification = 0;
        var unknownLocations = 0;
        var missingReportJournals = 0;
        string? cursor = null;
        var filter = new CaseQueueFilter(country, ReportedFromUtc: from, ReportedBeforeUtc: before);
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await queue.GetSearchableAsync(actor, 200, filter, cursor, ct).ConfigureAwait(false);
            if (page.Access != CaseQueueAccess.Allowed || page.Items.Count > 200)
                throw new InvalidOperationException("A complete authorized summary is unavailable.");
            foreach (var item in page.Items)
            {
                if (item.CaseId == Guid.Empty || !seen.Add(item.CaseId))
                    throw new InvalidOperationException("The case query changed while reading the summary.");
                if (!item.HasReportJournal)
                {
                    missingReportJournals++;
                    continue;
                }
                CaseReportDetails? report = null;
                if (placeId is not null || placeName is not null || includeObservations)
                {
                    report = await reports.GetSummaryEvidenceAsync(actor, item.CaseId, ct).ConfigureAwait(false);
                    if (report is null || report.CaseId != item.CaseId ||
                        report.AggregateVersion != item.AggregateVersion)
                        throw new InvalidOperationException("Case evidence changed or is unavailable.");
                }
                if (placeName is not null)
                {
                    var place = report!.Location;
                    if (place is null) { unknownLocations++; continue; }
                    if (country is not null && place.CountryCode != country) { unknownLocations++; continue; }
                    if (!new[] { place.Region, place.Area, place.Locality }.Any(label =>
                        string.Equals(label?.Trim(), placeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (place.Region is null || place.Area is null || place.Locality is null) unknownLocations++;
                        continue;
                    }
                }
                else if (placeId is not null)
                {
                    var place = report!.Location;
                    if (place is null || place.CountryCode != country ||
                        place.RegionId is null && place.AreaId is null && place.LocalityId is null)
                    {
                        unknownLocations++;
                        continue;
                    }
                    if (placeId != place.RegionId && placeId != place.AreaId && placeId != place.LocalityId)
                    {
                        if (place.RegionId is null || place.AreaId is null || place.LocalityId is null) unknownLocations++;
                        continue;
                    }
                }
                total++;
                switch (item.Classification)
                {
                    case "Unclassified": unclassified++; break;
                    case "Suspected" or "Probable" or "Confirmed" or "RuledOut": classified++; break;
                    default: unknownClassification++; break;
                }
                var category = item.Sector switch
                {
                    "Human" or "HumanHealth" => "Human",
                    "Animal" or "AnimalHealth" => "Animal",
                    _ => "Unknown sector"
                };
                categories[category] = categories.GetValueOrDefault(category) + 1;
                if (!includeObservations) continue;
                foreach (var observation in report!.Observations.Select(value => value.Trim())
                    .Where(value => value.Length is > 0 and <= 300).Distinct(StringComparer.OrdinalIgnoreCase))
                    observations[observation] = observations.GetValueOrDefault(observation) + 1;
            }
            cursor = page.NextCursor;
            if (cursor is not null && (page.Items.Count == 0 || !cursors.Add(cursor)))
                throw new InvalidOperationException("The case query could not be completed.");
        } while (cursor is not null);
        return new(total, classified, unclassified, unknownClassification, unknownLocations, missingReportJournals,
            Rank(categories), Rank(observations));
    }

    private static CaseReportingFrequency[] Rank(Dictionary<string, int> counts) => counts
        .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
        .Take(10).Select(pair => new CaseReportingFrequency(pair.Key, pair.Value)).ToArray();
}
