using Bit.Bonboarding;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private CancellationTokenSource? _guidanceCountdownCts;
    private int? _guidanceCountdownSeconds;
    private bool _selectingGuidanceSuggestion;

    private GuidancePlanStep? CurrentGuidanceStep => _generatedPlan?.Steps
        .SingleOrDefault(step => step.StepKey == _generatedTour?.CurrentStep?.Id);

    private void CancelGuidanceCountdown()
    {
        _guidanceCountdownCts?.Cancel();
        _guidanceCountdownSeconds = null;
    }

    private void BeginGuidanceCountdown(long sequence)
    {
        if (_generatedTour is not { Status: BonboardingTourStatus.Running } tour ||
            _guidanceNarrationEventId is not { } eventId || sequence != _latestResponseSequence ||
            _isPlayingResponse || _validationGuidanceText is not null || !string.IsNullOrWhiteSpace(InputText)) return;
        CancelGuidanceCountdown();
        _ = RunGuidanceCountdownAsync(tour, eventId, sequence);
    }

    private async Task RunGuidanceCountdownAsync(IBonboardingTourSession tour, string eventId, long sequence)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_uiObservationLifetime.Token);
        _guidanceCountdownCts = cancellation;
        var stepId = tour.CurrentStep?.Id;
        bool IsCurrent() => !cancellation.IsCancellationRequested && ReferenceEquals(tour, _generatedTour) &&
            tour.Status == BonboardingTourStatus.Running && tour.CurrentStep?.Id == stepId &&
            _guidanceNarrationEventId == eventId && sequence == _latestResponseSequence &&
            !_isPlayingResponse && string.IsNullOrWhiteSpace(InputText);
        try
        {
            for (var seconds = 5; seconds > 0 && IsCurrent(); seconds--)
            {
                _guidanceCountdownSeconds = seconds;
                await InvokeAsync(StateHasChanged);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
            }
            if (IsCurrent())
            {
                _guidanceCountdownSeconds = null;
                // This only requests presentation advancement. The existing server proof gate
                // still requires real input, confirmation, persistence and receipt evidence.
                await AdvanceGuidanceStepAsync(cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or Microsoft.JSInterop.JSException)
        {
            _guidanceNotice = "Use Next to continue when the connection is ready.";
        }
        finally
        {
            if (ReferenceEquals(_guidanceCountdownCts, cancellation))
            {
                _guidanceCountdownCts = null;
                _guidanceCountdownSeconds = null;
            }
            if (!_uiObservationLifetime.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SelectGuidanceSuggestionAsync(string suggestionId)
    {
        if (_selectingGuidanceSuggestion || _submittingUtterance || !string.IsNullOrWhiteSpace(InputText) ||
            _generatedTour?.Status != BonboardingTourStatus.Running ||
            CurrentGuidanceStep?.SuggestionIds.Contains(suggestionId) != true ||
            _generatedPlan?.Suggestions.TryGetValue(suggestionId, out var suggestion) != true || suggestion is null) return;
        _selectingGuidanceSuggestion = true;
        CancelGuidanceCountdown();
        try
        {
            await ControlGeneratedTourAsync("pause");
            if (_generatedTour?.Status != BonboardingTourStatus.Paused) return;
            // The normal durable conversation path reauthorizes and plans against fresh UI
            // observations. A suggestion is not a tool operation or an approval receipt.
            InputText = $"Please create a new guided plan for {suggestion.Label} (registered objective {suggestion.ObjectiveId}, control {suggestion.ControlId}).";
            await OnSend();
        }
        finally { _selectingGuidanceSuggestion = false; }
    }
}
