using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Geography;

namespace SwiftAid.UI.Components.Public.GuidedReportStepper;

public partial class GuidedReportStepper
{
    public string ComponentId => EllaiReportUiManifest.Definition.ComponentId;

    public EllaiUiManifest Describe() => EllaiReportUiManifest.Definition;

    public EllaiUiState GetState() => new(ComponentId, _submitting || _saving,
        _stepError is not null || _submitError is not null || _saveValidationAttempted && GetValidationGuidance().Count > 0 ||
            _step == 2 && ServerValidationRevision > 0 && ServerValidationRevision != _dismissedServerValidationRevision,
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
        ValidationIssues = GetValidationGuidance(),
        InputStates = GetInputStates()
    };

    private IReadOnlyList<UiInputState> GetInputStates()
    {
        var states = new List<UiInputState>();
        void Add(string control, string input, UiInputPresence presence) => states.Add(new(control, input, presence));
        static UiInputPresence TextState(string? value, bool valid) => string.IsNullOrWhiteSpace(value)
            ? UiInputPresence.NotProvided : valid ? UiInputPresence.Entered : UiInputPresence.Invalid;

        if (_step == 0)
        {
            Add("report.subject", "report.subject-kind", _subjectCategory is null ? UiInputPresence.NotProvided : UiInputPresence.Entered);
            if (_subjectCategory == ReportSubjectCategory.AnimalHealth)
                Add("report.subject", "report.animal-scope", HasCompleteSubject ? UiInputPresence.Entered : UiInputPresence.NotProvided);
        }
        else if (_step == 1)
        {
            Add("report.narrative", "report.original-statement", TextState(Draft.Narrative, (Draft.Narrative?.Length ?? 0) <= MaximumNarrativeCharacters));
            var observations = Draft.GetObservations();
            Add("report.narrative", "report.other-observations", observations.Count == 0 ? UiInputPresence.NotProvided :
                observations.Count <= MaximumObservations && observations.All(value => value.Length <= MaximumObservationCharacters) &&
                (Draft.ObservationsText?.Length ?? 0) <= 20000 ? UiInputPresence.Entered : UiInputPresence.Invalid);
            if (HasCountryCatalogue)
            {
                Add("report.time-location", "report.time-precision", Draft.OccurrencePrecision == ReportedTimePrecision.Unknown ? UiInputPresence.Unknown : UiInputPresence.Entered);
                if (Draft.OccurrencePrecision != ReportedTimePrecision.Unknown)
                    Add("report.time-location", "report.occurrence-date", Draft.OccurrenceDate.HasValue ? UiInputPresence.Entered : TextState(Draft.OccurrenceDateText, false));
                if (Draft.OccurrencePrecision == ReportedTimePrecision.Exact)
                {
                    Add("report.time-location", "report.occurrence-time", TextState(Draft.OccurrenceTime, TimeOnly.TryParse(Draft.OccurrenceTime, out _)));
                    Add("report.time-location", "report.utc-offset", TextState(Draft.OccurrenceOffset, HasValidOccurrenceOffset()));
                }
                Add("report.time-location", "report.occurrence-country", TextState(Draft.CountryCode, CanConfirmCountry));
                if (ShowRegistryPicker)
                    Add("report.time-location", "report.place-search", CurrentPlaceSelection is null ? UiInputPresence.Unknown : UiInputPresence.Entered);
                if (ShowManualPlaceFieldsGrid)
                {
                    static UiInputPresence PlaceState(PresenceState presence, string? value) => presence != PresenceState.Known
                        ? UiInputPresence.Unknown : PublicReportDraft.IsValidPlaceName(presence, value)
                            ? UiInputPresence.Entered : UiInputPresence.Invalid;
                    Add("report.time-location", "report.region", PlaceState(Draft.RegionPresence, Draft.Region));
                    Add("report.time-location", "report.area", PlaceState(Draft.AreaPresence, Draft.Area));
                    Add("report.time-location", "report.locality", PlaceState(Draft.LocalityPresence, Draft.Locality));
                }
            }
        }
        else if (_step == 2 && HasProcessingPolicy)
        {
            Add("report.confirmations", "report.review-confirmed", Draft.ReportReviewConfirmed ? UiInputPresence.Confirmed : UiInputPresence.NotProvided);
            if (CanConfirmCountry)
                Add("report.confirmations", "report.country-confirmed", Draft.CountryConfirmed ? UiInputPresence.Confirmed : UiInputPresence.NotProvided);
            Add("report.confirmations", "report.processing-confirmed", Draft.ProcessingConfirmed ? UiInputPresence.Confirmed : UiInputPresence.NotProvided);
        }
        return states;
    }

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
            AddIfInvalid(ServerValidationRevision > 0 && ServerValidationRevision != _dismissedServerValidationRevision,
                "report.server-validation-unknown");
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
