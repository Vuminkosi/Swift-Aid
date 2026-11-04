using SwiftAid.Contracts.Cases;

namespace SwiftAid.UI.Components.Cases;

/// <summary>Applies presentation-only filters to the authorized timeline entries already loaded.</summary>
public static class CaseTimelineEntryFilter
{
    public const string AllEvents = "*";
    public const string RelatedActivity = "kind:derived";
    public const string StaffActions = "kind:staff";
    public const string EllaiActivity = "agent:all";
    public const string EllaiEnrichment = "agent:enrichment";
    public const string EllaiFollowUp = "agent:followup";
    public const string Escalations = "kind:escalation";

    public static IReadOnlyList<CaseTimelineEntry> Apply(
        IEnumerable<CaseTimelineEntry> entries,
        string? eventType,
        DateTime? fromLocalDate,
        DateTime? throughLocalDate)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var from = fromLocalDate?.Date;
        var through = throughLocalDate?.Date;
        if (from is not null && through is not null && from.Value > through.Value) return [];
        var selected = string.IsNullOrWhiteSpace(eventType) ? AllEvents : eventType;
        bool InDate(DateTimeOffset value) => (from is null || value.ToLocalTime().Date >= from.Value) &&
            (through is null || value.ToLocalTime().Date <= through.Value);
        var results = new List<CaseTimelineEntry>();
        foreach (var entry in entries)
        {
            if (selected is EllaiActivity or EllaiEnrichment or EllaiFollowUp)
            {
                var activities = (entry.AgentActivities ?? []).Where(activity => InDate(activity.RecordedAtUtc) &&
                    (selected == EllaiActivity || selected == EllaiEnrichment && (activity.RoleId is "ops.enrichment" or "ops.quality") ||
                     selected == EllaiFollowUp && activity.RoleId == "ops.followup")).ToArray();
                if (activities.Length > 0) results.Add(entry with { AgentActivities = activities });
                continue;
            }
            var matches = selected == AllEvents || selected == RelatedActivity && entry.Kind == CaseTimelineEntryKind.Derived ||
                selected == StaffActions && entry.Kind == CaseTimelineEntryKind.Authoritative ||
                selected == Escalations && entry.Kind == CaseTimelineEntryKind.Authoritative && (entry.EventType is "CaseEscalationRaised" or "CaseEscalationAcknowledged" or "CaseEscalationActionRecorded" or "CaseEscalationClosed") ||
                string.Equals(entry.EventType, selected, StringComparison.Ordinal);
            if (matches && InDate(entry.RecordedAtUtc))
                results.Add(selected == StaffActions ? entry with { AgentActivities = [], AgentActivitiesTruncated = false } : entry);
        }
        return results.OrderBy(entry => entry.RecordedAtUtc).ThenBy(entry => entry.AggregateVersion).ToArray();
    }
}
