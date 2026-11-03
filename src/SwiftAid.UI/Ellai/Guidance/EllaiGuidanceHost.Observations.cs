using Microsoft.JSInterop;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private readonly CancellationTokenSource _uiObservationLifetime = new();
    private readonly SemaphoreSlim _uiObservationGate = new(1, 1);
    private IJSObjectReference? _uiObservationModule;
    private UiObservationReport? _latestGuidanceObservation;
    private string? _unavailableObservedStepId;

    private async Task MonitorUiObservationsAsync()
    {
        var token = _uiObservationLifetime.Token;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do
            {
                await InvokeAsync(() => ObserveGuidanceIterationAsync(token));
                timer.Period = _generatedTour is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(1);
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            await _uiObservationGate.WaitAsync();
            try
            {
                if (_uiObservationModule is not null) await _uiObservationModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            finally { _uiObservationGate.Release(); }
        }
    }

    private async Task ObserveGuidanceIterationAsync(CancellationToken token)
    {
        try
        {
            await ReportCurrentUiAsync(token);
            await UpdateValidationGuidanceAsync(token);
            await RetryGuidanceNarrationAsync(token);
            UpdateUnavailableGuidanceTarget();
            await AdvanceValidatedInputAsync(token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // An individual transport timeout must not end the parent observer.
            _latestGuidanceObservation = null;
        }
        catch (HttpRequestException)
        {
            _latestGuidanceObservation = null;
        }
        catch (JSException)
        {
            _latestGuidanceObservation = null;
        }
    }

    private async Task UpdateValidationGuidanceAsync(CancellationToken token)
    {
        if (_guidanceFeatureId != EllaiReportUiManifest.FeatureId || _generatedTour is not { } tour ||
            CurrentGuidanceStep is not { } step || _guidanceAnchors is null ||
            _latestGuidanceObservation is not { } observation) return;
        var issue = observation.ValidationInputIds?.Select(EllaiReportValidationGuidance.Find)
            .FirstOrDefault(item => item is not null);
        var nextId = issue?.InputId;
        if (_validationInputId == nextId && _validationTargetControlId == (issue is null ? null : step.ControlId))
        {
            return;
        }
        if (_validationTargetControlId is { } previous &&
            EllaiReportUiManifest.All.FirstOrDefault(control => control.ControlId == previous) is { } descriptor)
            _guidanceAnchors[previous] = descriptor.AnchorBindingKey;
        _validationInputId = nextId;
        _validationGuidanceText = issue?.Message;
        _validationTargetControlId = issue is null ? null : step.ControlId;
        if (issue is not null) _guidanceAnchors[step.ControlId] = issue.Anchor;
        CancelGuidanceCountdown();
        // Bit.Bonboarding 10.7 RefreshAsync measures the previously resolved target.
        // Re-enter the same presentation step to resolve its dynamic anchor again;
        // this does not advance the durable application checkpoint.
        await tour.GoToAsync(step.StepKey, token);
        StateHasChanged();
        if (ReferenceEquals(tour, _generatedTour) && _narrateValidation is { } narrate)
            await narrate(issue?.InputId, issue is null ? null : observation.ContextStamp, token);
    }

    private async Task RetryGuidanceNarrationAsync(CancellationToken token)
    {
        if (!_guidanceNarrationRetryPending || _guidanceNarrationEventId is not null || _guidanceNarrationCts is not null ||
            DateTimeOffset.UtcNow < _guidanceNarrationRetryAtUtc || _narrateValidation is not { } retry ||
            _generatedTour?.Status != Bit.Bonboarding.BonboardingTourStatus.Running ||
            CurrentGuidanceStep is not { } step || _latestGuidanceObservation is not { } observation) return;
        var issue = _guidanceFeatureId == EllaiReportUiManifest.FeatureId
            ? observation.ValidationInputIds?.Select(EllaiReportValidationGuidance.Find).FirstOrDefault(item => item is not null)
            : null;
        if (issue?.InputId != _validationInputId ||
            issue is not null && _validationTargetControlId != step.ControlId) return;
        await retry(issue?.InputId, issue is null ? null : observation.ContextStamp, token);
    }


    private void UpdateUnavailableGuidanceTarget()
    {
        if (_generatedTour?.Status is not (Bit.Bonboarding.BonboardingTourStatus.Running or Bit.Bonboarding.BonboardingTourStatus.Waiting) ||
            CurrentGuidanceStep is not { } step || _latestGuidanceObservation is not { } observation) return;
        if (_validationGuidanceText is not null) return;
        if (step.ControlId is "report.submit" or "report.receipt" &&
            Services.GetService(typeof(EllaiDisplayedReceiptContext)) is EllaiDisplayedReceiptContext { Current: not null }) return;
        // Accepted input can legitimately disappear when the wizard moves forward.
        var completed = observation.CompletedControlIds?.Contains(step.ControlId) == true;
        var state = observation.ControlStates?.SingleOrDefault(item => item.ControlId == step.ControlId);
        if (completed || state?.State == UiObservationState.Ready)
        {
            if (_unavailableObservedStepId == step.StepKey)
            {
                _unavailableObservedStepId = null;
                if (_guidanceReplanRequired && _guidanceNotice == "The current control is unavailable. Find another way to continue.")
                {
                    _guidanceReplanRequired = false;
                    _guidanceNotice = null;
                    StateHasChanged();
                }
            }
            return;
        }
        if (_guidanceReplanRequired) return;
        _guidanceReplanRequired = true;
        _unavailableObservedStepId = step.StepKey;
        _guidanceNotice = "The current control is unavailable. Find another way to continue.";
        StateHasChanged();
    }

    private Task ReportCurrentUiAsync(CancellationToken token) => ReportUiForFeatureAsync(_guidanceFeatureId, token);

    private async Task ReportUiForFeatureAsync(string? featureId, CancellationToken token)
    {
        _latestGuidanceObservation = null;
        if (_sessionView is not { Status: "Active" } session) return;
        await _uiObservationGate.WaitAsync(token);
        try
        {
            _uiObservationModule ??= await JS.InvokeAsync<IJSObjectReference>("import", token,
                "./_content/SwiftAid.UI/js/ellai-ui-observations.js");
            var requestedControls = featureId switch
            {
                EllaiNavigationManifest.FeatureId => EllaiNavigationManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiReportUiManifest.FeatureId => EllaiReportUiManifest.All.Select(control => control.ControlId).ToArray(),
                EllaiDashboardUiManifest.FeatureId => EllaiDashboardUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiCaseListUiManifest.FeatureId => EllaiCaseListUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiCaseDetailUiManifest.FeatureId => EllaiCaseDetailUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiCommunityUiManifest.FeatureId => EllaiCommunityUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiSurveillanceUiManifest.FeatureId => EllaiSurveillanceUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiInvestigationUiManifest.FeatureId => EllaiInvestigationUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiMonitoringUiManifest.FeatureId => EllaiMonitoringUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiOrganizationUiManifest.FeatureId => EllaiOrganizationUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                _ => null,
            };
            var report = await _uiObservationModule.InvokeAsync<UiObservationReport>("collect", token,
                new object?[] { requestedControls });
            if (report is null) return;
            var path = "/" + Navigation.ToBaseRelativePath(Navigation.Uri).Split('?', '#')[0].TrimEnd('/');
            report = report with { CurrentNavigationId = path == "/report" ? EllaiReportUiManifest.NavigationId :
                EllaiNavigationManifest.ResolveNavigationId(path) };
            report = report with { Platform = OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsIOS() ? "ios" : "web" };
            if (report.IsValid() && _sessionView?.SessionId == session.SessionId)
            {
                var accepted = await ReviewClient.ReportUiObservationAsync(session.SessionId, report, token);
                if (accepted && _sessionView?.SessionId == session.SessionId && _sessionView.Status == "Active")
                    _latestGuidanceObservation = report;
            }
        }
        catch (JSException) { /* Missing observations prevent guidance admission; text submission remains available. */ }
        catch (JSDisconnectedException) { }
        finally { _uiObservationGate.Release(); }
    }
}
