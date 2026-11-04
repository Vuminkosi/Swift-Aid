using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftAid.Application.Ellai;

/// <summary>Current-turn observation trace. Prior history and missing traces
/// require independent handling; this is not a permission or replay decision.</summary>
public sealed record EllaiToolTrace(int Version, bool HistoryIncluded, IReadOnlyList<EllaiObservedToolResult> Observations,
    IReadOnlyList<string>? HistoryTurnIds = null)
{
    public const int MaximumBytes = 60_000;
    private static readonly JsonSerializerOptions CompactJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string? Serialize(IReadOnlyList<EllaiObservedToolResult>? observations, bool historyIncluded,
        IReadOnlyList<string>? historyTurnIds = null)
    {
        if (observations is null) return null;
        var json = JsonSerializer.Serialize(new EllaiToolTrace(historyTurnIds is null ? 1 : 2, historyIncluded, observations, historyTurnIds));
        return IsValid(json) ? json : null;
    }

    /// <summary>Persist the complete provenance inherited by this answer, within the existing trace limits.</summary>
    public static string? SerializeFlattened(IReadOnlyList<EllaiObservedToolResult>? observations, bool historyIncluded,
        IReadOnlyList<EllaiObservedToolResult> inherited)
    {
        if (observations is null) return null;
        var combined = inherited.Concat(observations)
            .DistinctBy(item => JsonSerializer.Serialize(item), StringComparer.Ordinal).ToArray();
        var json = JsonSerializer.Serialize(new EllaiToolTrace(3, historyIncluded, combined), CompactJson);
        return IsValid(json) ? json : null;
    }

    public static bool IsValid(string json)
        => TryRead(json, out _);

    public static bool TryRead(string json, out EllaiToolTrace? trace)
    {
        trace = null;
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<EllaiToolTrace>(json);
            var valid = parsed is { Version: 1 or 2 or 3, Observations: not null } &&
                (parsed.Version == 1 || parsed.Version == 3 && parsed.HistoryTurnIds is null || parsed.Version == 2 && parsed.HistoryTurnIds is { Count: <= 6 } ids &&
                    (parsed.HistoryIncluded || ids.Count == 0) &&
                    ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 256) &&
                    ids.Distinct(StringComparer.Ordinal).Count() == ids.Count) &&
                parsed.Observations.All(item => item is not null && !string.IsNullOrWhiteSpace(item.ToolName) &&
                    item.ToolName.Length <= 128 && !string.IsNullOrWhiteSpace(item.ReasonCode) &&
                    item.ReasonCode.Length <= 128 && item.Evidence is { Count: <= 20 } &&
                    item.Evidence.All(reference => reference is not null) &&
                    (item.ImportListScope is null || item.ToolName == EllaiToolNames.Imports && item.ImportListScope.IsValid()) &&
                    (item.CaseListQuery is null || item.ToolName == EllaiToolNames.State && item.CaseListQuery.IsValid()) &&
                    (item.QueueProjectionVersion is null || item.CaseListQuery is not null &&
                        item.QueueProjectionVersion == EllaiObservedToolResult.CurrentQueueProjectionVersion) &&
                    (item.CaseListDigest is null || item.CaseListQuery?.PlaceId is not null &&
                        item.CaseListDigest is { Length: 64 } &&
                        item.CaseListDigest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F')) &&
                    (item.RetainedClassificationFilter is null || item.ToolName == EllaiToolNames.Evidence &&
                        item.RetainedClassificationFilter.IsValid() && item.CaseSnapshotExpiresAtUtc is not null) &&
                    (item.RetainedCaseReplay is null || item.ToolName is EllaiToolNames.Topic or EllaiToolNames.Evidence &&
                        (item.Truncated || item.RetainedCaseReplay.ClassificationFilter is not null || item.RetainedCaseReplay.PlaceId is not null) &&
                        item.CaseSnapshotExpiresAtUtc is not null && item.RetainedCaseReplay.IsValid() &&
                        (item.ToolName != EllaiToolNames.Topic || item.RetainedCaseReplay.Field is null)) &&
                    (item.StaticFeatureVersion is null || item.ToolName == EllaiToolNames.State && item.CaseListQuery is null &&
                        item.StaticFeatureVersion == EllaiObservedToolResult.CurrentStaticFeatureVersion) &&
                    (item.CaseSnapshotExpiresAtUtc is null || item.ToolName is EllaiToolNames.Topic or EllaiToolNames.Evidence) &&
                    (item.ClockVersion is null || item.ToolName == EllaiToolNames.Now &&
                        item.ClockVersion == EllaiObservedToolResult.CurrentClockVersion) &&
                    (item.CapabilityVersion is null || item.ToolName == EllaiToolNames.Capabilities &&
                        item.CapabilityVersion == EllaiObservedToolResult.CurrentCapabilityVersion) &&
                    (item.ReceiptListDigest is null || item.ToolName == EllaiToolNames.Receipts &&
                        item.ReceiptListDigest is { Length: 64 } &&
                        item.ReceiptListDigest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F')) &&
                    (item.ReceiptLookupId is null || item.ToolName == EllaiToolNames.Receipts &&
                        item.ReceiptListDigest is not null && EllaiObservedToolResult.IsCanonicalReceiptId(item.ReceiptLookupId) &&
                        item.Evidence.Count == 1 && item.Evidence[0] is { SourceKind: "Receipt", FieldPath: null } receipt &&
                        receipt.ResourceId == item.ReceiptLookupId && receipt.EvidenceId == item.ReceiptLookupId) &&
                    (item.CountReplayScope is null || item.ToolName == EllaiToolNames.OwnedCaseCount && item.CountReplayScope.IsValid()) &&
                    (item.UiCatalogueReplayScope is null || item.ToolName == EllaiToolNames.UiCatalogue && item.UiCatalogueReplayScope.IsValid()) &&
                    (item.UiObservationReplayScope is null || item.ToolName == EllaiToolNames.UiObserve && item.UiObservationReplayScope.IsValid()));
            if (valid) trace = parsed;
            return valid;
        }
        catch (JsonException) { return false; }
    }
}
