using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Report assistance routes are mounted under the existing owner-checked Ellai session groups.</summary>
public static class ReportVoiceEndpoints
{
    public static async Task<IResult> ConsentAsync(HttpContext http, string sessionId,
        ReportVoiceConsentRequest? request, [FromServices] ReportVoiceGatewayService service, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        if (request?.Confirmed != true) return Results.BadRequest();
        if (!await ValidateCookieRequestAsync(http).ConfigureAwait(false)) return Results.BadRequest();
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        return await service.GrantAsync(sessionId, actor.ActorId, ct).ConfigureAwait(false)
            ? Results.NoContent() : Results.NotFound();
    }

    public static async Task<IResult> ExtractAsync(HttpContext http, string sessionId,
        ReportVoiceExtractionRequest? request, [FromServices] ReportVoiceGatewayService service, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        if (string.IsNullOrWhiteSpace(request?.Transcript) ||
            request.Transcript.Length > ReportVoiceExtraction.MaximumTranscriptLength) return Results.BadRequest();
        if (!await ValidateCookieRequestAsync(http).ConfigureAwait(false)) return Results.BadRequest();
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var result = await service.ExtractAsync(sessionId, actor.ActorId, request.Transcript, ct).ConfigureAwait(false);
        return Results.Json(result);
    }

    public static async Task<IResult> TranscribeSavedAsync(HttpContext http, string sessionId,
        [FromServices] IEllaiSessionStore sessions, [FromServices] EllaiVoiceTranscriptionService transcription,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        if (http.Request.ContentLength is not (> 0 and <= EllaiVoiceTranscriptionService.MaximumAudioBytes) ||
            !string.Equals(http.Request.ContentType, "audio/wav", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
        if (!await ValidateCookieRequestAsync(http).ConfigureAwait(false)) return Results.BadRequest();
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actor.ActorId || session.Status != "Active") return Results.NotFound();
        var result = await transcription.TranscribeSavedReportAsync(sessionId, http.Request.Body,
            http.Request.ContentLength, ct).ConfigureAwait(false);
        var current = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (current is null || current.Status != "Active" || current.OwnerActorId != actor.ActorId ||
            current.ScopeEpoch != session.ScopeEpoch) return Results.NotFound();
        return result is null ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Saved recording transcription is unavailable.") : Results.Json(result);
    }

    private static async Task<bool> ValidateCookieRequestAsync(HttpContext http)
    {
        // The guest group validates before resolving its guest principal. Revalidating
        // against the substituted principal would reject the already checked token.
        if (http.Items[EllaiGuestEndpoints.ValidatedAntiforgeryRequest] is true) return true;
        if (!http.Request.Headers.ContainsKey("Cookie")) return true;
        try
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
