using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.UI.Ellai.Guidance;

public partial class EllaiGuidanceHost
{
    private EllaiPublicReportDraftContext? _reportVoiceContext;
    private bool _reportVoiceCapture;
    private readonly SemaphoreSlim _reportVoiceExtractionGate = new(1, 1);

    private async Task<bool> StartReportVoiceCaptureAsync(EllaiVoiceCaptureMode mode)
    {
        var started = false;
        await InvokeAsync(async () =>
        {
            if (_sessionView is not { Status: "Active" }) await SelectModeAsync(EllaiMode.Ask);
            if (_sessionView is not { Status: "Active" } session) return;
            if (!await ReviewClient.GrantReportVoiceConsentAsync(session.SessionId, CancellationToken.None)) return;
            ShowComposer = true;
            await StartVoiceCaptureAsync(mode, reportCapture: true);
            started = _isRecordingAudio;
        });
        return started;
    }

    private async Task<bool> TranscribeSavedReportVoiceAsync(
        Func<CancellationToken, Task<byte[]>> readWave, string captureId, CancellationToken cancellationToken)
    {
        var success = false;
        await InvokeAsync(async () =>
        {
            if (_sessionView is not { Status: "Active" }) await SelectModeAsync(EllaiMode.Ask);
            if (_sessionView is not { Status: "Active" } session) return;
            if (!await ReviewClient.GrantReportVoiceConsentAsync(session.SessionId, cancellationToken)) return;
            byte[]? wave = null;
            try
            {
                wave = await readWave(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ReviewClient.TranscribeSavedReportVoiceAsync(session.SessionId, wave, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(result?.Transcript)) return;
                QueueReportVoiceTurn(session.SessionId, Guid.NewGuid().ToString("N"), captureId, result.Transcript);
                success = true;
            }
            finally
            {
                if (wave is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(wave);
            }
        });
        return success;
    }

    private Task StopReportVoiceCaptureAsync() => InvokeAsync(StopVoiceCaptureAsync);

    private void QueueReportVoiceTurn(string sessionId, string turnId, string? captureId, string transcript)
    {
        var context = _reportVoiceContext;
        var lease = context?.AdmitVoiceTurn(sessionId, turnId, captureId);
        if (context is null || lease is null) return;
        _ = ProcessReportVoiceTurnAsync(context, lease, sessionId, transcript);
    }

    private async Task ProcessReportVoiceTurnAsync(
        EllaiPublicReportDraftContext context, ReportVoiceTurnLease lease, string sessionId, string transcript)
    {
        try
        {
            var appended = false;
            await InvokeAsync(() => appended = context.AppendVoiceTranscript(lease.Generation, transcript));
            if (!appended) return;
            await _reportVoiceExtractionGate.WaitAsync(lease.CancellationToken);
            ReportVoiceExtractionResponse? response;
            try
            {
                response = await ReviewClient.ExtractReportVoiceAsync(sessionId, transcript, lease.CancellationToken);
            }
            finally
            {
                _reportVoiceExtractionGate.Release();
            }
            await InvokeAsync(() => context.CompleteVoiceTurn(lease.Generation, transcript, response));
        }
        catch (Exception)
        {
            try { await InvokeAsync(() => context.CompleteVoiceTurn(lease.Generation, transcript, null)); }
            catch (InvalidOperationException) { }
        }
    }
}
