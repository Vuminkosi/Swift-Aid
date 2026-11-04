using Microsoft.JSInterop;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private string? _reportInputStep;
    private int _reportInputIndex = -1;
    private string? _reportInputText;
    private string? _reportInputAnchor;

    private bool HasReportInputSequence => _guidanceFeatureId == EllaiReportUiManifest.FeatureId &&
        CurrentGuidanceStep is { } step && EllaiReportInputTour.ForSection(step.ControlId).Count > 0;

    private async Task InitializeReportInputAsync(CancellationToken ct)
    {
        if (!HasReportInputSequence || CurrentGuidanceStep is not { } step)
        {
            _reportInputStep = null;
            _reportInputText = _reportInputAnchor = null;
            return;
        }
        var key = $"{_generatedPlan?.PlanId}:{step.StepKey}";
        if (_reportInputStep == key || _validationGuidanceText is not null) return;
        _reportInputStep = key;
        _reportInputIndex = -1;
        await MoveReportInputAsync(1, ct);
    }

    private async Task<bool> MoveReportInputAsync(int direction, CancellationToken ct)
    {
        if (!HasReportInputSequence || _validationGuidanceText is not null ||
            CurrentGuidanceStep is not { } step || _generatedTour is not { } tour || _guidanceAnchors is null)
            return false;
        var key = $"{_generatedPlan?.PlanId}:{step.StepKey}";
        if (_reportInputStep != key)
        {
            _reportInputStep = key;
            _reportInputIndex = -1;
        }
        var inputs = EllaiReportInputTour.ForSection(step.ControlId);
        _uiObservationModule ??= await JS.InvokeAsync<IJSObjectReference>("import", ct,
            "./_content/SwiftAid.UI/js/ellai-ui-observations.js");
        for (var index = _reportInputIndex + direction; index >= 0 && index < inputs.Count; index += direction)
        {
            var input = inputs[index];
            if (!await _uiObservationModule.InvokeAsync<bool>("prepareReportInput", ct, input.Anchor)) continue;
            if (!ReferenceEquals(tour, _generatedTour) || CurrentGuidanceStep?.StepKey != step.StepKey) return true;
            CancelGuidanceCountdown();
            _reportInputIndex = index;
            _reportInputText = input.Instruction;
            _reportInputAnchor = input.Anchor;
            _guidanceAnchors[step.ControlId] = input.Anchor;
            _guidanceNotice = null;
            await tour.GoToAsync(step.StepKey, ct);
            StateHasChanged();
            return true;
        }
        if (direction > 0)
        {
            _reportInputIndex = inputs.Count;
            _reportInputText = _reportInputAnchor = null;
        }
        return false;
    }
}
