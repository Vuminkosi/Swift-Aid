using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Components.Public.GuidedReportStepper;

public partial class GuidedReportStepper
{
    public string ComponentId => EllaiReportUiManifest.Definition.ComponentId;

    public EllaiUiManifest Describe() => EllaiReportUiManifest.Definition;

    public EllaiUiState GetState() => new(ComponentId, _submitting || _saving,
        _stepError is not null || _submitError is not null,
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
             CanSubmit && !_submitting ? UiObservationState.Ready : UiObservationState.Disabled)]);
}
