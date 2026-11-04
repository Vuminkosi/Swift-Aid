using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Gateway.Ellai;

public static class EllaiVoiceWebSocketEndpoint
{
    public static async Task<IResult> HandleAsync(
        HttpContext http,
        [FromRoute] string sessionId,
        [FromServices] EllaiVoiceTranscriptionService transcription,
        [FromServices] EllaiSessionService sessions,
        CancellationToken cancellationToken)
    {
        if (!http.WebSockets.IsWebSocketRequest || !IsSameOrigin(http)
            || !http.WebSockets.WebSocketRequestedProtocols.Contains(SwiftAid.Contracts.Ellai.EllaiSpeechEvent.Protocol))
        {
            return Results.BadRequest();
        }

        var principal = http.User;
        if (await sessions.GetOwnedViewAsync(sessionId, principal, cancellationToken)
                .ConfigureAwait(false) is not { Status: "Active" }
            || !await transcription.HasConsentAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "Voice transcription is unavailable for this session.");
        }

        var captureId = http.Request.Query["voiceCaptureId"].FirstOrDefault();
        var preferences = EllaiSpeechPreferences.Read(http.Request.Query);
        if (preferences is null || string.IsNullOrWhiteSpace(captureId) || captureId.Length > 64
            || !long.TryParse(http.Request.Query["voiceCaptureOffsetSamples"].FirstOrDefault(), out var offset)
                && http.Request.Query.ContainsKey("voiceCaptureOffsetSamples")
            || offset is < 0 or > SwiftAid.Contracts.Ellai.EllaiSpeechEvent.MaximumPosition)
        {
            return Results.BadRequest();
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync(SwiftAid.Contracts.Ellai.EllaiSpeechEvent.Protocol).ConfigureAwait(false);
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SwiftAid.Ellai.VoiceTransport");
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var audio = new EllaiVoiceWebSocketStream(socket, Math.Max(0, offset));
        logger.LogInformation("Voice connected: session={SessionId} capture={CaptureId} transport=WebSocket offsetSamples={OffsetSamples}.",
            sessionId, captureId, Math.Max(0, offset));
        try
        {
            var result = await transcription.TranscribeAsync(
                sessionId,
                SwiftAid.Contracts.Ellai.EllaiVoiceCaptureMode.Live,
                audio,
                contentLength: null,
                cancellationToken,
                captureId,
                Math.Max(0, offset),
                submissionPrincipal: http.Request.Query["serverSubmission"] == "true" ? principal : null,
                speechPreferences: preferences)
                .ConfigureAwait(false);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(result is null ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure,
                    result is null ? "Voice stream needs recovery." : "Voice stream completed.", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable,
                    "Voice stream cancelled.", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning("Voice transport failed: session={SessionId} capture={CaptureId} failure={FailureType}.",
                sessionId, captureId, exception.GetType().Name);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError,
                    "Voice stream failed.", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            SegmentedAudioMetrics.Record(audio.ReceivedBytes, audio.SuppressedSamples, audio.DuplicateEvents, audio.MissingEvents);
            logger.LogInformation("Voice disconnected: session={SessionId} capture={CaptureId} bytes={AudioBytes} elapsedMs={ElapsedMs} close={CloseStatus} suppressedSamples={SuppressedSamples} duplicates={Duplicates} missing={Missing}.",
                sessionId, captureId, audio.ReceivedBytes, started.ElapsedMilliseconds, socket.CloseStatus, audio.SuppressedSamples, audio.DuplicateEvents, audio.MissingEvents);
        }

        return Results.Empty;
    }

    private static bool IsSameOrigin(HttpContext http)
    {
        var origin = http.Request.Headers.Origin.FirstOrDefault();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        // A reverse proxy may terminate TLS before the Gateway sees the request.
        // Exact authority matching still rejects cross-site WebSocket attempts.
        return string.Equals(originUri.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
