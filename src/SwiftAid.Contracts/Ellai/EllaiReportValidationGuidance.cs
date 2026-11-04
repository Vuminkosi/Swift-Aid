namespace SwiftAid.Contracts.Ellai;

/// <summary>Application-authored, value-free explanations and targets for report validation.</summary>
public static class EllaiReportValidationGuidance
{
    /// <summary>The registered presentation control owning each trusted correction target.</summary>
    public static string? ControlFor(string inputId) => inputId switch
    {
        "report.subject-kind" or "report.animal-scope" => "report.subject",
        "report.original-statement" or "report.original-statement-too-long" or "report.other-observations" or
            "report.too-many-observations" or "report.observation-too-long" or "report.observations-total-too-long" => "report.narrative",
        "report.occurrence-date" or "report.invalid-exact-occurrence" or "report.occurrence-time" or "report.utc-offset" or
            "report.place-search" or "report.invalid-draft-country" or "report.region" or "report.area" or "report.locality" => "report.time-location",
        "report.review-confirmed" or "report.text-limits-changed" or "report.server-validation-unknown" or "report.country-catalogue-unavailable" or
            "report.privacy-notice-unavailable" or "report.processing-policy-unavailable" or "report.country-required" or
            "report.country-confirmed" or "report.processing-confirmed" => "report.review",
        _ => null
    };

    public static EllaiUiValidationIssue? Find(string inputId) => inputId switch
    {
        "report.subject-kind" => new(inputId, "ellai-report-subject-kind", "Choose whether the concern is about human health or animal health."),
        "report.animal-scope" => new(inputId, "ellai-report-animal-scope", "Choose whether the concern is about an individual animal, herd, flock, or another group."),
        "report.original-statement" => new(inputId, "ellai-report-original-statement", "Describe what happened or add an observation."),
        "report.original-statement-too-long" => new(inputId, "ellai-report-original-statement", "The description is too long. Shorten it to the limit shown on the form."),
        "report.other-observations" => new(inputId, "ellai-report-other-observations", "The observations exceed the allowed length or count. Check the limits shown on the form."),
        "report.too-many-observations" => new(inputId, "ellai-report-other-observations", "There are too many observations. Remove some to meet the limit shown on the form."),
        "report.observation-too-long" => new(inputId, "ellai-report-other-observations", "An observation is too long. Shorten it to the per-observation limit shown on the form."),
        "report.observations-total-too-long" => new(inputId, "ellai-report-other-observations", "The observations text is too long overall. Shorten or remove some text."),
        "report.occurrence-date" => new(inputId, "ellai-report-occurrence-date", "Enter the reported date as year-month-day, or choose that the time is unknown."),
        "report.invalid-exact-occurrence" => new(inputId, "ellai-report-occurrence-date", "The date, time and UTC offset cannot be combined. Check the date or choose that the time is unknown."),
        "report.occurrence-time" => new(inputId, "ellai-report-occurrence-time", "Enter a valid reported time, for example zero eight colon thirty."),
        "report.utc-offset" => new(inputId, "ellai-report-utc-offset", "Enter a valid time difference from UTC, such as plus zero two colon zero zero."),
        "report.place-search" => new(inputId, "ellai-report-place", "Complete the reported place or leave uncertain place names unknown."),
        "report.invalid-draft-country" => new(inputId, "ellai-report-place", "The saved country code is invalid. Choose a country or clear the country field before saving the draft."),
        "report.region" => new(inputId, "ellai-report-region", "Enter the reported region using at most 200 characters, or leave it unknown."),
        "report.area" => new(inputId, "ellai-report-area", "Enter the reported area or district using at most 200 characters, or leave it unknown."),
        "report.locality" => new(inputId, "ellai-report-locality", "Enter the reported locality using at most 200 characters, or leave it unknown."),
        "report.review-confirmed" => new(inputId, "ellai-report-review-confirmed", "Review the original statement and details, then confirm your review."),
        "report.text-limits-changed" => new(inputId, "ellai-report-return-details", "Go back to the details and correct any missing or over-limit report text."),
        "report.server-validation-unknown" => new(inputId, "ellai-report-return-details", "The service rejected the report but did not identify a field. Go back and review the details; your entries remain here."),
        "report.country-catalogue-unavailable" => new(inputId, "ellai-report-country-confirmed", "The country choices are unavailable, so the report cannot be sent yet. Save a draft if available and try again later."),
        "report.privacy-notice-unavailable" => new(inputId, "ellai-report-privacy-unavailable", "The privacy notice is unavailable, so the report cannot be sent yet. Save a draft if available and try again later."),
        "report.processing-policy-unavailable" => new(inputId, "ellai-report-policy-unavailable", "The report-handling information is unavailable, so the report cannot be sent yet. Save a draft if available and try again later."),
        "report.country-required" => new(inputId, "ellai-report-return-details", "Go back to the details and choose a supported event country before confirming it."),
        "report.country-confirmed" => new(inputId, "ellai-report-country-confirmed", "Confirm the country where the event happened."),
        "report.processing-confirmed" => new(inputId, "ellai-report-processing-confirmed", "Review and acknowledge how this report is handled."),
        _ => null
    };
}
