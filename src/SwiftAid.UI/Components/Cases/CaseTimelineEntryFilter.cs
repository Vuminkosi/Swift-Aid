using SwiftAid.Contracts.Cases;

namespace SwiftAid.UI.Components.Cases;

/// <summary>Applies presentation-only filters to the authorized timeline entries already loaded.</summary>
public static class CaseTimelineEntryFilter
{
    private const string AllEvents = "*";
    private const string RelatedActivity = "kind:derived";

    public static IReadOnlyList<CaseTimelineEntry> Apply(
        IEnumerable<CaseTimelineEntry> entries,
        string? eventType,
        DateTime? fromLocalDate,
        DateTime? throughLocalDate)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var from = fromLocalDate?.Date;
        var through = throughLocalDate?.Date;
        if (from is not null && through is not null && from.Value > through.Value)
        {
            return Array.Empty<CaseTimelineEntry>();
        }

        var selectedEventType = string.IsNullOrWhiteSpace(eventType) ? AllEvents : eventType;

        return entries
            .Where(entry => selectedEventType == AllEvents
                || (selectedEventType == RelatedActivity && entry.Kind == CaseTimelineEntryKind.Derived)
                || string.Equals(entry.EventType, selectedEventType, StringComparison.Ordinal))
            .Where(entry => from is null || entry.RecordedAtUtc.ToLocalTime().Date >= from.Value)
            .Where(entry => through is null || entry.RecordedAtUtc.ToLocalTime().Date <= through.Value)
            .OrderBy(entry => entry.RecordedAtUtc)
            .ThenBy(entry => entry.AggregateVersion)
            .ToArray();
    }
}
