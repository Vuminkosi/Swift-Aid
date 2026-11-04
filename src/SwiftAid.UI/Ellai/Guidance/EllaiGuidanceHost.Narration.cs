using Bit.Bonboarding;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private string? _guidanceNarrationEventId;
    private CancellationTokenSource? _guidanceNarrationCts;
    private CancellationTokenSource? _validationNarrationCts;
    private string? _validationNarrationEventId;

    private async Task NarrateValidationAsync(IBonboardingTourSession tour, long version, string message)
    {
        var code = EllaiValidationPrompts.CodeFor(message);
        if (code is null || !ReferenceEquals(tour, _generatedTour) || _sessionView is null ||
            _generatedPlan is null || _guidanceProposalId is null) return;
        var step = _generatedPlan.Steps.SingleOrDefault(item => item.StepKey == tour.CurrentStep?.Id);
        if (step?.ControlId is not ("report.subject" or "report.narrative" or "report.time-location")) return;
        var request = new EllaiValidationNarrationRequest(_guidanceProposalId, _generatedPlan.PlanId,
            step.StepKey, version, code, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / EllaiValidationNarrationRequest.WindowSeconds,
            _speechPreferences.EnglishVoiceId);
        var sessionId = _sessionView.SessionId;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_uiObservationLifetime.Token);
        _validationNarrationCts = cancellation;
        _validationNarrationEventId = request.EventId;
        try
        {
            var response = await ReviewClient.PrepareValidationNarrationAsync(sessionId, request, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(tour, _generatedTour) &&
                _sessionView?.SessionId == sessionId && _validationNarrationEventId == request.EventId && response is not null)
                OnResponseReceived(response);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        finally
        {
            if (ReferenceEquals(_validationNarrationCts, cancellation)) _validationNarrationCts = null;
        }
    }

    private void CancelValidationNarration()
    {
        _validationNarrationEventId = null;
        _validationNarrationCts?.Cancel();
    }
    private DateTimeOffset _guidanceNarrationRetryAtUtc;
    private bool _guidanceNarrationRetryPending;

    private async Task NarrateGuidanceStepAsync(IBonboardingTourSession tour, long version,
        string? validationInputId = null, string? observationContextStamp = null)
    {
        if (!ReferenceEquals(tour, _generatedTour) || tour.Status != BonboardingTourStatus.Running ||
            _sessionView is null || _generatedPlan is null || _guidanceProposalId is null) return;
        if (validationInputId is null && _validationInputId is not null)
        {
            validationInputId = _validationInputId;
            observationContextStamp = _latestGuidanceObservation?.ContextStamp;
        }
        var step = _generatedPlan.Steps.SingleOrDefault(item => item.StepKey == tour.CurrentStep?.Id);
        if (step is null) return;
        var request = new EllaiGuidanceNarrationRequest(_guidanceProposalId, _generatedPlan.PlanId,
            step.StepKey, version, _speechPreferences.EnglishVoiceId)
        { ValidationInputId = validationInputId, ObservationContextStamp = observationContextStamp };
        if (_guidanceNarrationEventId == request.EventId) return;
        _guidanceNarrationCts?.Cancel();
        _guidanceNarrationRetryPending = false;
        StopResponseAudio();
        _guidanceNarrationEventId = request.EventId;
        _latestResponseText = validationInputId is null ? step.Explanation : _validationGuidanceText;
        _latestResponseSequence = 0;
        _latestResponseAudioSequences.Clear();
        var sessionId = _sessionView.SessionId;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_uiObservationLifetime.Token);
        _guidanceNarrationCts = cancellation;
        var accepted = false;
        try
        {
            await ReportCurrentUiAsync(cancellation.Token);
            var response = await ReviewClient.PrepareGuidanceNarrationAsync(sessionId, request, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(tour, _generatedTour) &&
                _sessionView?.SessionId == sessionId && _guidanceNarrationEventId == request.EventId && response is not null)
            {
                accepted = true;
                OnResponseReceived(response);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { /* The validated step remains readable and usable. */ }
        finally
        {
            if (!accepted && !cancellation.IsCancellationRequested && ReferenceEquals(tour, _generatedTour) &&
                _sessionView?.SessionId == sessionId && _guidanceNarrationEventId == request.EventId)
            {
                // A failed request may have been accepted remotely; a later retry uses the
                // same event identity so the server can return its existing response.
                _guidanceNarrationEventId = null;
                _guidanceNarrationRetryPending = true;
                _guidanceNarrationRetryAtUtc = DateTimeOffset.UtcNow.AddSeconds(5);
            }
            if (ReferenceEquals(_guidanceNarrationCts, cancellation)) _guidanceNarrationCts = null;
            if (!_uiObservationLifetime.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private void EndGuidanceNarration()
    {
        CancelValidationNarration();
        CancelGuidanceCountdown();
        _guidanceNarrationRetryPending = false;
        if (_guidanceNarrationEventId is null) return;
        _guidanceNarrationEventId = null;
        StopResponseAudio();
    }
}
