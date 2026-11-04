using System.Text.Json;
using SwiftAid.Application.Classification;

namespace SwiftAid.Application.Surveillance;

/// <summary>Derived evidence references only. Receipt time is deliberately absent.</summary>
public sealed record SurveillanceObservation(Guid CaseId, Guid SourceEventId, long ReportRevision,
    Guid? ClassificationResultId, string ReportingSource, DateTimeOffset? OccurredAtUtc,
    string[] Areas, string[] Syndromes, string[] QualityLimitations);

public sealed record DetectionAssessment(string Method, string Area, string Syndrome,
    DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc, int Observed, int ReportingActivity,
    int IndependentSources, int BaselineObserved, int BaselineReportingActivity, double? Expected,
    string Comparison, bool Candidate, string Severity, string[] Limitations,
    SurveillanceObservation[] Evidence, string EvidenceHash);

/// <summary>
/// Versioned synthetic evaluation rule. Counts describe reports, never population incidence.
/// Geographic membership uses supplied administrative identities, not invented coordinates.
/// </summary>
public static class SurveillanceDetector
{
    public const string Version = "reported-area-window-synthetic-v1";
    public static readonly string[] Syndromes = ["gastrointestinal", "respiratory", "febrile", "neurological", "rash-fever"];
    public const int WindowDays = 3;
    public const int BaselineDays = 28;
    public const int MinimumCases = 3;
    public const int MinimumSources = 2;

    public static DetectionAssessment Assess(string area, string syndrome, DateTimeOffset end,
        IReadOnlyCollection<SurveillanceObservation> observations)
    {
        if (!Syndromes.Contains(syndrome, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(area) ||
            end.Offset != TimeSpan.Zero || end.TimeOfDay != TimeSpan.Zero)
            throw new ArgumentException("A known syndrome, area and UTC day boundary are required.");
        var start = end.AddDays(-WindowDays);
        var baselineStart = start.AddDays(-BaselineDays);
        var evidence = observations.Where(o => o.Areas.Contains(area, StringComparer.Ordinal) &&
                o.OccurredAtUtc >= baselineStart && o.OccurredAtUtc < end)
            .GroupBy(o => o.CaseId).Select(g => g.OrderByDescending(o => o.ReportRevision).ThenBy(o => o.SourceEventId).First())
            .OrderBy(o => o.CaseId).ToArray();
        var recent = evidence.Where(o => o.OccurredAtUtc >= start).ToArray();
        var baseline = evidence.Where(o => o.OccurredAtUtc < start).ToArray();
        var matches = recent.Where(o => o.Syndromes.Contains(syndrome, StringComparer.Ordinal)).ToArray();
        var baselineCount = baseline.Count(o => o.Syndromes.Contains(syndrome, StringComparer.Ordinal));
        var sources = matches.Select(o => o.ReportingSource).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Count();
        var limitations = new List<string> { "synthetic-rule-not-validated-for-production", "reporting-actors-are-a-proxy-for-source-independence", "report-counts-not-disease-incidence", "administrative-area-not-distance-cluster", "population-denominator-unknown" };
        // At least seven observed baseline dates and twenty reports prevent interpreting an empty baseline as zero incidence.
        var baselineReady = baseline.Length >= 20 && baseline.Select(o => o.OccurredAtUtc!.Value.UtcDateTime.Date).Distinct().Count() >= 7;
        double? expected = baselineReady ? (double)baselineCount / baseline.Length * recent.Length : null;
        if (!baselineReady) limitations.Add("insufficient-baseline");
        if (evidence.Any(o => o.Syndromes.Length == 0)) limitations.Add("some-reports-have-no-classified-syndrome");
        limitations.AddRange(evidence.SelectMany(o => o.QualityLimitations));
        var comparison = expected is null ? "InsufficientBaseline" : matches.Length > expected ? "IncreasedReportedShare" : "NoIncreaseInReportedShare";
        var hash = ClassificationFingerprint.Hash(JsonSerializer.Serialize(new { Version, area, syndrome, end, evidence }));
        return new(Version, area, syndrome, start, end, matches.Length, recent.Length, sources, baselineCount,
            baseline.Length, expected, comparison, matches.Length >= MinimumCases && sources >= MinimumSources,
            "Unknown", limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), evidence, hash);
    }
}
