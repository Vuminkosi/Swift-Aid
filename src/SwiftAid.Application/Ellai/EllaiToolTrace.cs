using System.Text;
using System.Text.Json;

namespace SwiftAid.Application.Ellai;

/// <summary>Current-turn observation trace. Prior history and missing traces
/// require independent handling; this is not a permission or replay decision.</summary>
public sealed record EllaiToolTrace(int Version, bool HistoryIncluded, IReadOnlyList<EllaiObservedToolResult> Observations)
{
    public const int MaximumBytes = 60_000;

    public static string? Serialize(IReadOnlyList<EllaiObservedToolResult>? observations, bool historyIncluded)
    {
        if (observations is null) return null;
        var json = JsonSerializer.Serialize(new EllaiToolTrace(1, historyIncluded, observations));
        return IsValid(json) ? json : null;
    }

    public static bool IsValid(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) return false;
        try
        {
            var trace = JsonSerializer.Deserialize<EllaiToolTrace>(json);
            return trace is { Version: 1, Observations.Count: <= 256 } &&
                trace.Observations.All(item => item is not null && !string.IsNullOrWhiteSpace(item.ToolName) &&
                    item.ToolName.Length <= 128 && !string.IsNullOrWhiteSpace(item.ReasonCode) &&
                    item.ReasonCode.Length <= 128 && item.Evidence is { Count: <= 20 } &&
                    item.Evidence.All(reference => reference is not null));
        }
        catch (JsonException) { return false; }
    }
}
