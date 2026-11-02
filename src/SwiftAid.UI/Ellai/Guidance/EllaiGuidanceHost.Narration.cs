using Bit.Bonboarding;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private string? _guidanceNarrationEventId;
    private CancellationTokenSource? _guidanceNarrationCts;

    private async Task NarrateGuidanceStepAsync(IBonboardingTourSession tour, long version)
    {
        if (!ReferenceEquals(tour, _generatedTour) || tour.Status != BonboardingTourStatus.Running ||
            _sessionView is null || _generatedPlan is null || _guidanceProposalId is null) return;
        var step = _generatedPlan.Steps.SingleOrDefault(item => item.StepKey == tour.CurrentStep?.Id);
        if (step is null) return;
        var request = new EllaiGuidanceNarrationRequest(_guidanceProposalId, _generatedPlan.PlanId,
            step.StepKey, version, _speechPreferences.EnglishVoiceId);
        if (_guidanceNarrationEventId == request.EventId) return;
        StopResponseAudio();
        _guidanceNarrationEventId = request.EventId;
        _latestResponseText = step.Explanation;
        _latestResponseSequence = 0;
        _latestResponseAudioSequences.Clear();
        var sessionId = _sessionView.SessionId;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_uiObservationLifetime.Token);
        _guidanceNarrationCts = cancellation;
        try
        {
            await ReportCurrentUiAsync(cancellation.Token);
            var response = await ReviewClient.PrepareGuidanceNarrationAsync(sessionId, request, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(tour, _generatedTour) &&
                _sessionView?.SessionId == sessionId && _guidanceNarrationEventId == request.EventId && response is not null)
                OnResponseReceived(response);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { /* The validated step remains readable and usable. */ }
        finally
        {
            if (ReferenceEquals(_guidanceNarrationCts, cancellation)) _guidanceNarrationCts = null;
            if (!_uiObservationLifetime.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private void EndGuidanceNarration()
    {
        CancelGuidanceCountdown();
        if (_guidanceNarrationEventId is null) return;
        _guidanceNarrationEventId = null;
        StopResponseAudio();
    }
}
