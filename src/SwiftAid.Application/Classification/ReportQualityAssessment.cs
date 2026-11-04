using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Classification;

public sealed record ReportQualityFinding(string Code, string Field, string Explanation);
public sealed record ReportQualityAssessment(string Version, IReadOnlyList<ReportQualityFinding> Findings);

/// <summary>Reproducible source-data checks. Findings neither change evidence nor establish clinical urgency.</summary>
public static class ReportQualityRules
{
    public const string Version = "reported-facts-quality-v1";

    public static ReportQualityAssessment Assess(CaseReportEventData source, DateTimeOffset recordedAt)
    {
        var findings = new List<ReportQualityFinding>();
        var context = source switch
        {
            CaseReportEventData.HumanV2 human => human.Report.Context,
            CaseReportEventData.AnimalV2 animal => animal.Report.Context,
            _ => null
        };
        var evidence = source switch
        {
            CaseReportEventData.HumanV2 human => human.Report.Evidence,
            CaseReportEventData.AnimalV2 animal => animal.Report.Evidence,
            _ => null
        };
        if (context is null)
        {
            Add("legacy-context-unavailable", "context", "This source revision has no typed occurrence, capture or location context.");
        }
        else
        {
            if (!context.Occurrence.Value.HasValue)
                Add("occurrence-not-known", "occurrence", "Occurrence time is not known; receipt time must not substitute for it.");
            else
            {
                var occurrence = context.Occurrence.Value.Value;
                if (occurrence.Precision != ReportedTimePrecision.Exact)
                    Add("occurrence-not-exact", "occurrence", "Occurrence time retains the reported date or range precision.");
                if (occurrence.Value > recordedAt)
                    Add("occurrence-after-receipt", "occurrence", "The supplied occurrence timestamp is later than server receipt; verify time settings.");
                if (occurrence.Value > context.CapturedAtUtc)
                    Add("occurrence-after-capture", "occurrence", "The supplied occurrence timestamp is later than capture; verify the source dates.");
            }
            if (context.CapturedAtUtc is null)
                Add("capture-time-not-recorded", "capture", "Capture time is not recorded.");
            else if (context.CapturedAtUtc > recordedAt)
                Add("capture-after-receipt", "capture", "The supplied capture timestamp is later than server receipt; verify time settings.");
            if (context.Location.Precision != LocationPrecision.Exact)
                Add("geography-not-exact", "location", "The reported location is approximate or unknown; do not infer precise coordinates.");
            if (context.Location.Latitude.HasValue != context.Location.Longitude.HasValue)
                Add("incomplete-coordinate-pair", "location.coordinates", "Only one coordinate was supplied.");
            if (context.Location.Region.Presence != PresenceState.Known)
                Add("region-not-known", "location.region", "The reported region is not known.");
        }
        if (evidence is not null)
        {
            if (evidence.Narrative is null && evidence.Observations.Count == 0)
                Add("text-evidence-unavailable", "evidence", "Original evidence is retained, but no text is available for classification.");
            if (evidence.Observations.Select(o => ClassificationFingerprint.Normalize(o.Text)).Distinct(StringComparer.Ordinal).Count() < evidence.Observations.Count)
                Add("repeated-observation-text", "evidence.observations", "This revision repeats identical observation text; repetitions are not independent evidence.");
        }
        if (source is CaseReportEventData.AnimalV2 animalReport)
            foreach (var issue in animalReport.Report.Validate())
                Add("animal-count-relationship", issue.Field, issue.Message);
        return new(Version, findings);

        void Add(string code, string field, string explanation) => findings.Add(new(code, field, explanation));
    }
}
