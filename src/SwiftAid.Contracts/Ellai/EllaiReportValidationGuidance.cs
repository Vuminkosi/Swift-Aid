namespace SwiftAid.Contracts.Ellai;

/// <summary>Application-authored, value-free explanations and targets for report validation.</summary>
public static class EllaiReportValidationGuidance
{
    /// <summary>The registered presentation control owning each trusted correction target.</summary>
    public static string? ControlFor(string inputId) => inputId switch
    {
        "report.subject-kind" or "report.animal-scope" => "report.subject",
        "report.original-statement" or "report.original-statement-too-long" or "report.other-observations" or
            "report.too-many-observations" or "report.observation-too-long" or "report.observations-total-too-long" or "report.age-years" => "report.narrative",
        "report.occurrence-date" or "report.invalid-exact-occurrence" or "report.occurrence-time" or "report.utc-offset" or
            "report.place-search" or "report.invalid-draft-country" or "report.region" or "report.area" or "report.locality" => "report.time-location",
        "report.review-confirmed" or "report.text-limits-changed" or "report.age-review" or "report.server-validation-unknown" or "report.country-catalogue-unavailable" or
            "report.privacy-notice-unavailable" or "report.processing-policy-unavailable" or "report.country-required" or
            "report.country-confirmed" or "report.processing-confirmed" => "report.review",
        _ => null
    };

    public static EllaiUiValidationIssue? Find(string inputId) => inputId switch
    {
        "report.subject-kind" => new(inputId, "ellai-report-subject-kind", "I haven't captured whether this concern is about a person or an animal. Choose Human health or Animal health to continue."),
        "report.animal-scope" => new(inputId, "ellai-report-animal-scope", "I haven't captured which animal or animal group this concern is about. Choose an individual animal, herd, flock, or another group."),
        "report.original-statement" => new(inputId, "ellai-report-original-statement", "I haven't captured what happened yet. Describe it or add a reported observation."),
        "report.age-years" => new(inputId, "ellai-report-age-years", "I can't capture the reported age as a valid whole number. Enter age in whole years, or leave it blank if unknown."),
        "report.original-statement-too-long" => new(inputId, "ellai-report-original-statement", "I can't capture the full description within the limit. Shorten it to the limit shown on the form."),
        "report.other-observations" => new(inputId, "ellai-report-other-observations", "I can't capture these observations within the allowed length or count. Check the limits shown on the form."),
        "report.too-many-observations" => new(inputId, "ellai-report-other-observations", "I can't capture all these observations because there are too many. Remove some to meet the limit shown on the form."),
        "report.observation-too-long" => new(inputId, "ellai-report-other-observations", "I can't capture this observation because it is too long. Shorten it to the per-observation limit shown on the form."),
        "report.observations-total-too-long" => new(inputId, "ellai-report-other-observations", "I can't capture all the observation text within the total limit. Shorten or remove some text."),
        "report.occurrence-date" => new(inputId, "ellai-report-occurrence-date", "I haven't captured the reported date. Enter it as year-month-day, or choose that the time is unknown."),
        "report.invalid-exact-occurrence" => new(inputId, "ellai-report-occurrence-date", "I can't capture this date, time, and UTC offset together. Check the date or choose that the time is unknown."),
        "report.occurrence-time" => new(inputId, "ellai-report-occurrence-time", "I haven't captured a valid reported local time. Enter it, or choose that the time is unknown."),
        "report.utc-offset" => new(inputId, "ellai-report-utc-offset", "I haven't captured a valid time difference from UTC. Enter one, such as plus zero two colon zero zero."),
        "report.place-search" => new(inputId, "ellai-report-place", "I haven't captured a reported place. Complete it, or leave uncertain place names unknown."),
        "report.invalid-draft-country" => new(inputId, "ellai-report-place", "I can't capture this saved country code because it is invalid. Choose a country or clear the country field before saving the draft."),
        "report.region" => new(inputId, "ellai-report-region", "I haven't captured a valid region. Enter the reported region using at most 200 characters, or leave it unknown."),
        "report.area" => new(inputId, "ellai-report-area", "I haven't captured a valid area or district. Enter it using at most 200 characters, or leave it unknown."),
        "report.locality" => new(inputId, "ellai-report-locality", "I haven't captured a valid locality. Enter it using at most 200 characters, or leave it unknown."),
        "report.review-confirmed" => new(inputId, "ellai-report-review-confirmed", "I haven't captured your review confirmation. Review the original statement and details, then confirm."),
        "report.text-limits-changed" => new(inputId, "ellai-report-return-details", "I haven't captured report text that meets the current limits. Select Return to details; I will point to the first text field to correct."),
        "report.age-review" => new(inputId, "ellai-report-return-details", "I haven't captured the reported age as a valid whole number. Select Return to details; I will highlight the age field. Leave it blank if unknown."),
        "report.server-validation-unknown" => new(inputId, "ellai-report-return-details", "The service rejected the report but did not identify which detail it couldn't accept. Go back and review the highlighted details; your entries remain here."),
        "report.country-catalogue-unavailable" => new(inputId, "ellai-report-country-confirmed", "I can't capture or confirm the event country because the country choices are unavailable. Save a draft if available and try again later."),
        "report.privacy-notice-unavailable" => new(inputId, "ellai-report-privacy-unavailable", "I can't capture your review confirmation because the privacy notice is unavailable. Save a draft if available and try again later."),
        "report.processing-policy-unavailable" => new(inputId, "ellai-report-policy-unavailable", "I can't capture your report-handling confirmation because the policy information is unavailable. Save a draft if available and try again later."),
        "report.country-required" => new(inputId, "ellai-report-return-details", "I haven't captured a supported event country. Go back to the details, choose one, then confirm it."),
        "report.country-confirmed" => new(inputId, "ellai-report-country-confirmed", "I haven't captured your confirmation of the event country. Confirm the country where the event happened."),
        "report.processing-confirmed" => new(inputId, "ellai-report-processing-confirmed", "I haven't captured your report-handling confirmation. Review and acknowledge how this report is handled."),
        _ => null
    };
}
