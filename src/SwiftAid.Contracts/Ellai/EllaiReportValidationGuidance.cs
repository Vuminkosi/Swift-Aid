namespace SwiftAid.Contracts.Ellai;

/// <summary>Application-authored, value-free explanations and targets for report validation.</summary>
public static class EllaiReportValidationGuidance
{
    public static EllaiUiValidationIssue? Find(string inputId) => inputId switch
    {
        "report.subject-kind" => new(inputId, "ellai-report-subject-kind", "Choose whether the concern is about human health or animal health."),
        "report.animal-scope" => new(inputId, "ellai-report-animal-scope", "Choose whether the concern is about an individual animal, herd, flock, or another group."),
        "report.original-statement" => new(inputId, "ellai-report-original-statement", "Describe what happened or add an observation. Keep the description within 6,000 characters."),
        "report.other-observations" => new(inputId, "ellai-report-other-observations", "Use at most 20 observations, 1,000 characters each and 20,000 characters in total."),
        "report.occurrence-date" => new(inputId, "ellai-report-occurrence-date", "Add the reported date or choose that the time is unknown."),
        "report.occurrence-time" => new(inputId, "ellai-report-occurrence-time", "Enter a valid reported time."),
        "report.utc-offset" => new(inputId, "ellai-report-utc-offset", "Enter a valid time difference from UTC, such as plus zero two colon zero zero."),
        "report.place-search" => new(inputId, "ellai-report-place", "Complete the reported place or leave uncertain place names unknown."),
        "report.region" => new(inputId, "ellai-report-region", "Enter the reported region using at most 200 characters, or leave it unknown."),
        "report.area" => new(inputId, "ellai-report-area", "Enter the reported area or district using at most 200 characters, or leave it unknown."),
        "report.locality" => new(inputId, "ellai-report-locality", "Enter the reported locality using at most 200 characters, or leave it unknown."),
        "report.review-confirmed" => new(inputId, "ellai-report-review-confirmed", "Review the original statement and details, then confirm your review."),
        "report.country-required" => new(inputId, "ellai-report-return-details", "Go back to the details and choose a supported event country before confirming it."),
        "report.country-confirmed" => new(inputId, "ellai-report-country-confirmed", "Confirm the country where the event happened."),
        "report.processing-confirmed" => new(inputId, "ellai-report-processing-confirmed", "Review and acknowledge how this report is handled."),
        _ => null
    };
}
