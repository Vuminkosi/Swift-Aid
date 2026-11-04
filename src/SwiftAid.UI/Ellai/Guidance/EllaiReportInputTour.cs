namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Application-owned input targets; never model-provided selectors or values.</summary>
public static class EllaiReportInputTour
{
    public sealed record Input(string Anchor, string Instruction);

    public static IReadOnlyList<Input> ForSection(string controlId) => controlId switch
    {
        "report.subject" => [
            new("ellai-report-subject-kind", "Choose whether the report concerns a person or an animal."),
            new("ellai-report-gender", "Choose the reported gender, or leave it unknown."),
            new("ellai-report-animal-scope", "Choose the reported animal or group scope."),
            new("ellai-report-death-related", "Choose whether the concern is reported as related to a death; keep unknown when not reported."),
            new("ellai-report-continue-details", "Choose Continue to details to confirm the subject and open the capture fields.")],
        "report.narrative" => [
            new("ellai-report-original-statement", "Describe what happened in the reporter's own words."),
            new("ellai-report-age-years", "Enter age in completed years if reported; otherwise leave it blank."),
            new("ellai-report-core-symptoms", "Select only the core symptoms that were reported. Unselected does not mean absent."),
            new("ellai-report-signs-of-dehydration", "Select only reported signs of dehydration. Do not infer signs or a diagnosis."),
            new("ellai-report-additional-symptoms-and-signs", "Select any additional reported symptoms and signs."),
            new("ellai-report-eating-and-activity", "Select only reported changes in eating and activity."),
            new("ellai-report-other-observations", "Add other reported observations, or leave blank if there are none."),
            // Narrative and time/location share one form page. Its validation proof
            // is issued by Review report, after the reporter visits both sections.
            .. ForSection("report.time-location")],
        "report.time-location" => [
            new("ellai-report-onset-precision", "Choose how precisely the onset of symptoms or signs is known. Keep Unknown if not reported."),
            new("ellai-report-onset-date", "Choose the reported onset date."),
            new("ellai-report-onset-time", "Enter the reported onset time."),
            new("ellai-report-onset-utc-offset", "Enter the reported onset time's UTC offset."),
            new("ellai-report-time-precision", "Choose how precisely the time of the event is known."),
            new("ellai-report-occurrence-date", "Choose the reported event date."),
            new("ellai-report-occurrence-time", "Enter the reported event time."),
            new("ellai-report-utc-offset", "Enter the event time's UTC offset."),
            new("ellai-report-place", "Choose the country where the event happened."),
            new("ellai-report-place-search", "Search for the reported place; use the offered place selectors."),
            new("ellai-report-place-browse", "You can also browse the place catalogue by area."),
            new("ellai-report-place-country", "Choose the reported country in the place catalogue."),
            new("ellai-report-place-region", "Choose the reported region or province after selecting the country."),
            new("ellai-report-place-locality", "Choose the reported town, village, or locality after selecting its region."),
            new("ellai-report-region", "Enter the reported region, or leave it unknown."),
            new("ellai-report-area", "Enter the reported area or district, or leave it unknown."),
            new("ellai-report-locality", "Enter the reported locality, or leave it unknown."),
            new("ellai-report-open-review", "Choose Review report to validate these details and review the captured information.")],
        "report.review" => [
            new("ellai-report-review-confirmed", "Review the captured report and confirm it only when correct."),
            new("ellai-report-country-confirmed", "Confirm the reported event country.")],
        "report.confirmations" => [new("ellai-report-processing-confirmed", "Review and acknowledge how the report is handled.")],
        _ => []
    };
}
