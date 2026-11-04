using System.Text.Json;

namespace SwiftAid.Application.Ellai;

/// <summary>Validated scenario manifest loaded from fixtures or admin payloads.
/// Manifests are data: finite cycles, explicit synthetic/live label, bounded
/// capabilities. Clinical content or unbounded loops are rejected at load.</summary>
public sealed record ScenarioManifest(
    string ManifestId,
    string Purpose,
    string ScenarioId,
    bool Synthetic,
    IReadOnlyList<string> AllowedCapabilities,
    int TotalCycles);

public static class ScenarioManifestLoader
{
    public static (ScenarioManifest? Manifest, string? Error) Load(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var manifestId = root.GetProperty("manifestId").GetString();
            var purpose = root.GetProperty("purpose").GetString();
            var scenarioId = root.GetProperty("scenarioId").GetString();
            var synthetic = root.TryGetProperty("synthetic", out var s) && s.GetBoolean();
            var totalCycles = root.GetProperty("totalCycles").GetInt32();
            var caps = root.TryGetProperty("allowedCapabilities", out var c)
                ? c.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>();

            if (string.IsNullOrWhiteSpace(manifestId) || string.IsNullOrWhiteSpace(purpose)
                || string.IsNullOrWhiteSpace(scenarioId))
            {
                return (null, "Manifest requires manifestId, purpose and scenarioId.");
            }

            if (totalCycles is < 1 or > 12)
            {
                return (null, "Manifest totalCycles must be 1..12.");
            }

            if (caps.Any(c => c.Contains("clinical", StringComparison.OrdinalIgnoreCase)))
            {
                return (null, "Manifest capabilities must not include clinical scope.");
            }

            var text = json.ToLowerInvariant();
            string[] clinical = ["diagnos", "triage", "symptom", "outbreak", "treatment"];
            if (clinical.Any(text.Contains))
            {
                return (null, "Manifest content must not include clinical predictions or advice.");
            }

            return (new ScenarioManifest(manifestId, purpose, scenarioId, synthetic, caps, totalCycles), null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, $"Invalid manifest JSON: {ex.Message}");
        }
    }
}
