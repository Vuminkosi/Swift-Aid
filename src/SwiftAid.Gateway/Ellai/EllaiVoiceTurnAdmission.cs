using System.Security.Claims;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Admits confirmed voice turns through the same fenced PostgreSQL outbox
/// boundary as typed messages. No client receipt or microphone EOF is required.</summary>
internal sealed class EllaiVoiceTurnAdmission(
    IServiceProvider services, string sessionId, ClaimsPrincipal principal,
    EllaiVoiceTurnCoordinator? coordinator = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SubmitCompletedAsync()
    {
        if (coordinator is null) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var turn in coordinator.GetCompletedTurns(sessionId))
            {
                if (!turn.ServerSubmission || turn.Admission is not null) continue;
                var result = await SubmitAsync(turn.TurnId.ToString("N"), turn.Transcript).ConfigureAwait(false);
                coordinator.RecordSubmission(sessionId, turn.TurnId, result);
                services.GetService<ILogger<EllaiVoiceTurnAdmission>>()?.LogInformation(
                    "Voice turn admission: session={SessionId} turn={TurnId} sequence={Sequence} outcome={Outcome}.",
                    sessionId, turn.TurnId, turn.Sequence, result.Status);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<EllaiUtteranceSubmitResult> SubmitAsync(string utteranceId, string transcript)
    {
        using var timing = EllaiPipelineMetrics.Measure("voice_admission");
        // Once confirmed, admission must survive a client transport disconnect.
        // The bounded attempt still stops when the host shuts down.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await using var scope = services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IEllaiSessionStore>();
                var session = await new EllaiSessionService(store).GetOwnedViewAsync(
                    sessionId, principal, timeout.Token).ConfigureAwait(false);
                if (session is null) return new(EllaiClientOperationStatus.Unauthorized, null);
                var request = new SubmitUtteranceRequest(utteranceId, sessionId, session.SessionVersion,
                    transcript, null, "typed", session.SessionVersion);
                var result = await scope.ServiceProvider.GetRequiredService<EllaiUtteranceSubmissionService>()
                    .SubmitAsync(sessionId, request, principal, timeout.Token,
                        coordinator is null ? "recorded-voice" : "live-voice").ConfigureAwait(false);
                if (result.Status != EllaiClientOperationStatus.Conflict) return result;
            }
        }
        catch (Exception exception)
        {
            services.GetService<ILogger<EllaiVoiceTurnAdmission>>()?.LogWarning(
                "Ellai confirmed voice admission failed ({FailureType}).", exception.GetType().Name);
        }
        return new(EllaiClientOperationStatus.Unavailable, null);
    }

    public void Dispose() => _gate.Dispose();
}
