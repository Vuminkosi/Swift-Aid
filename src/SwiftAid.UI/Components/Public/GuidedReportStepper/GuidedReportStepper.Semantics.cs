using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.UI.Components.Public.GuidedReportStepper;

public partial class GuidedReportStepper
{
    public string ComponentId => EllaiReportUiManifest.Definition.ComponentId;

    public EllaiUiManifest Describe() => EllaiReportUiManifest.Definition;

    public EllaiUiState GetState() => new(ComponentId, _submitting || _saving,
        _stepError is not null || _submitError is not null || _saveValidationAttempted && GetValidationGuidance().Count > 0,
        [new("report.entry", UiObservationState.Ready),
         new("report.save", !OnSaveDraft.HasDelegate ? UiObservationState.Unmounted :
             CanSaveDraft && !_saving ? UiObservationState.Ready : UiObservationState.Disabled,
             _saveState == DraftSaveState.Saved && _confirmedDraftSave is { } saved &&
             saved.SourceDraftId == CurrentDraftId &&
             (DraftMatches(Draft, saved.SourceDraft) || DraftMatches(Draft, saved.SavedDraft))),
         new("report.subject", _step == 0 ? UiObservationState.Ready : UiObservationState.Unmounted, _step > 0 && HasCompleteSubject),
         new("report.narrative", _step == 1 ? UiObservationState.Ready : UiObservationState.Unmounted, _step == 2),
         new("report.time-location", _step != 1 ? UiObservationState.Unmounted :
             HasCountryCatalogue ? UiObservationState.Ready : UiObservationState.Disabled, _step == 2),
         new("report.confirmations", _step == 2 && HasProcessingPolicy ? UiObservationState.Ready : UiObservationState.Unmounted,
             _step == 2 && CanSubmit && !_submitting),
         new("report.review", _step == 2 ? UiObservationState.Ready : UiObservationState.Unmounted),
         new("report.submit", _step != 2 ? UiObservationState.Unmounted :
             CanSubmit && !_submitting ? UiObservationState.Ready : UiObservationState.Disabled)])
    {
        ValidationIssues = GetValidationGuidance()
    };

    private IReadOnlyList<EllaiUiValidationIssue> GetValidationGuidance()
    {
        var issues = new List<EllaiUiValidationIssue>();
        void AddIfInvalid(bool invalid, string inputId)
        {
            if (invalid && EllaiReportValidationGuidance.Find(inputId) is { } issue) issues.Add(issue);
        }
        if (_step == 0)
        {
            AddIfInvalid(_subjectCategory is null, "report.subject-kind");
            AddIfInvalid(_subjectCategory == ReportSubjectCategory.AnimalHealth && !HasCompleteSubject, "report.animal-scope");
            return issues;
        }
        if (_step == 2)
        {
            AddIfInvalid(!ReportTextIsValid, "report.text-limits-changed");
            AddIfInvalid(!HasCountryCatalogue, "report.country-catalogue-unavailable");
            AddIfInvalid(!HasPrivacyNotice, "report.privacy-notice-unavailable");
            AddIfInvalid(!HasProcessingPolicy, "report.processing-policy-unavailable");
            AddIfInvalid(!Draft.ReportReviewConfirmed, "report.review-confirmed");
            AddIfInvalid(HasCountryCatalogue && !CanConfirmCountry, "report.country-required");
            AddIfInvalid(CanConfirmCountry && !Draft.CountryConfirmed, "report.country-confirmed");
            AddIfInvalid(HasProcessingPolicy && !Draft.ProcessingConfirmed, "report.processing-confirmed");
            return issues;
        }
        if (_step != 1 || !_detailsValidationAttempted && !_saveValidationAttempted) return [];
        var observations = Draft.GetObservations();
        if (_saveValidationAttempted)
        {
            var country = Draft.CountryCode?.Trim();
            AddIfInvalid((Draft.Narrative?.Length ?? 0) > 6000, "report.original-statement-too-long");
            AddIfInvalid(!string.IsNullOrEmpty(country) && (country.Length != 2 || !country.All(char.IsAsciiLetter)), "report.invalid-draft-country");
            AddIfInvalid(observations.Count > 20, "report.too-many-observations");
            AddIfInvalid(observations.Any(value => value.Length > 1000), "report.observation-too-long");
            AddIfInvalid((Draft.ObservationsText?.Length ?? 0) > 20000, "report.observations-total-too-long");
            AddIfInvalid(!PublicReportDraft.IsValidPlaceName(Draft.RegionPresence, Draft.Region), "report.region");
            AddIfInvalid(!PublicReportDraft.IsValidPlaceName(Draft.AreaPresence, Draft.Area), "report.area");
            AddIfInvalid(!PublicReportDraft.IsValidPlaceName(Draft.LocalityPresence, Draft.Locality), "report.locality");
            AddIfInvalid(Draft.OccurrencePrecision == ReportedTimePrecision.Exact && !Draft.OccurrenceDate.HasValue, "report.occurrence-date");
            AddIfInvalid(Draft.OccurrencePrecision == ReportedTimePrecision.Exact && !TimeOnly.TryParse(Draft.OccurrenceTime, out _), "report.occurrence-time");
            AddIfInvalid(Draft.OccurrencePrecision == ReportedTimePrecision.Exact && !HasValidOccurrenceOffset(), "report.utc-offset");
            AddIfInvalid(Draft.OccurrencePrecision == ReportedTimePrecision.Exact && Draft.OccurrenceDate.HasValue &&
                HasValidExactTime() && !Draft.HasValidExactOccurrence(), "report.invalid-exact-occurrence");
            return issues;
        }
        AddIfInvalid(string.IsNullOrWhiteSpace(Draft.Narrative) && observations.Count == 0, "report.original-statement");
        AddIfInvalid((Draft.Narrative?.Length ?? 0) > MaximumNarrativeCharacters, "report.original-statement-too-long");
        AddIfInvalid(observations.Count > MaximumObservations, "report.too-many-observations");
        AddIfInvalid(observations.Any(value => value.Length > MaximumObservationCharacters), "report.observation-too-long");
        AddIfInvalid((Draft.ObservationsText?.Length ?? 0) > 20000, "report.observations-total-too-long");
        AddIfInvalid(Draft.OccurrencePrecision != ReportedTimePrecision.Unknown && !Draft.OccurrenceDate.HasValue,
            "report.occurrence-date");
        AddIfInvalid(Draft.OccurrencePrecision == ReportedTimePrecision.Exact && !TimeOnly.TryParse(Draft.OccurrenceTime, out _),
            "report.occurrence-time");
        AddIfInvalid(Draft.OccurrencePrecision == ReportedTimePrecision.Exact && !HasValidOccurrenceOffset(), "report.utc-offset");
        AddIfInvalid(Draft.OccurrencePrecision == ReportedTimePrecision.Exact && Draft.OccurrenceDate.HasValue &&
            HasValidExactTime() && !Draft.HasValidExactOccurrence(), "report.invalid-exact-occurrence");
        AddIfInvalid(!PublicReportDraft.IsValidPlaceName(Draft.RegionPresence, Draft.Region), "report.region");
        AddIfInvalid(!PublicReportDraft.IsValidPlaceName(Draft.AreaPresence, Draft.Area), "report.area");
        AddIfInvalid(!PublicReportDraft.IsValidPlaceName(Draft.LocalityPresence, Draft.Locality), "report.locality");
        AddIfInvalid(!Draft.LocationUnknown && string.IsNullOrWhiteSpace(Draft.CountryCode) &&
            Draft.RegionPresence != PresenceState.Known && Draft.AreaPresence != PresenceState.Known &&
            Draft.LocalityPresence != PresenceState.Known, "report.place-search");
        return issues;
    }
}
