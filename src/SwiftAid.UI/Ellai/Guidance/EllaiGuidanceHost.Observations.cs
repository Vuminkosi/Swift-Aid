using Microsoft.JSInterop;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private readonly CancellationTokenSource _uiObservationLifetime = new();
    private readonly SemaphoreSlim _uiObservationGate = new(1, 1);
    private IJSObjectReference? _uiObservationModule;

    private async Task MonitorUiObservationsAsync()
    {
        var token = _uiObservationLifetime.Token;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do
            {
                await InvokeAsync(() => ReportCurrentUiAsync(token));
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

    private async Task ReportCurrentUiAsync(CancellationToken token)
    {
        if (_sessionView is not { Status: "Active" } session) return;
        await _uiObservationGate.WaitAsync(token);
        try
        {
            _uiObservationModule ??= await JS.InvokeAsync<IJSObjectReference>("import", token,
                "./_content/SwiftAid.UI/js/ellai-ui-observations.js");
            var report = await _uiObservationModule.InvokeAsync<UiObservationReport>("collect", token);
            report = report with { Platform = OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsIOS() ? "ios" : "web" };
            if (report.IsValid() && _sessionView?.SessionId == session.SessionId)
                await ReviewClient.ReportUiObservationAsync(session.SessionId, report, token);
        }
        catch (JSException) { /* Missing observations prevent guidance admission; text submission remains available. */ }
        catch (JSDisconnectedException) { }
        finally { _uiObservationGate.Release(); }
    }
}
