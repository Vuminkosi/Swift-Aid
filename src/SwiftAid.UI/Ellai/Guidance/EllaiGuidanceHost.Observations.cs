using Microsoft.JSInterop;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private readonly CancellationTokenSource _uiObservationLifetime = new();
    private readonly SemaphoreSlim _uiObservationGate = new(1, 1);
    private IJSObjectReference? _uiObservationModule;
    private UiObservationReport? _latestGuidanceObservation;

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

    private async Task ReportCurrentUiAsync(CancellationToken token)
    {
        _latestGuidanceObservation = null;
        if (_sessionView is not { Status: "Active" } session) return;
        await _uiObservationGate.WaitAsync(token);
        try
        {
            _uiObservationModule ??= await JS.InvokeAsync<IJSObjectReference>("import", token,
                "./_content/SwiftAid.UI/js/ellai-ui-observations.js");
            var requestedControls = _guidanceFeatureId switch
            {
                EllaiNavigationManifest.FeatureId => EllaiNavigationManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiReportUiManifest.FeatureId => EllaiReportUiManifest.All.Select(control => control.ControlId).ToArray(),
                EllaiDashboardUiManifest.FeatureId => EllaiDashboardUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiCaseListUiManifest.FeatureId => EllaiCaseListUiManifest.Controls.Select(control => control.ControlId).ToArray(),
                EllaiCaseDetailUiManifest.FeatureId => EllaiCaseDetailUiManifest.Controls.Select(control => control.ControlId).ToArray(),
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
