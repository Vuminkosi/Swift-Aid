using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Sync;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Public Ellai surface for anonymous guests. The signed guest
/// cookie binds each request to one opaque actor; guest sessions have no
/// workspace grants and their utterance path is restricted to public facts.</summary>
public static class EllaiGuestEndpoints
{
    public const string AuthenticationScheme = "SwiftAid.EllaiGuest";
    public const string GuestClaim = "swiftaid_ellai_guest";
    private const string GuestActorPrefix = "ellai-guest-";
    private static readonly TimeSpan GuestSessionLifetime = TimeSpan.FromHours(2);

    public static IResult ReadVoiceTurnState(
        HttpContext http, string sessionId, EllaiVoiceTurnCoordinator coordinator,
        long afterSequence = 0)
    {
        var snapshot = coordinator.GetSnapshot(sessionId);
        http.Response.Headers.CacheControl = "no-store, private";
        return Results.Ok(new EllaiVoiceTurnStatusDto(
            snapshot.State.ToString(),
            snapshot.Epoch,
            ToMilliseconds(snapshot.ConfirmedSpeech),
            ToMilliseconds(snapshot.Silence),
            snapshot.PartialTranscript,
            coordinator.GetCompletedTurns(sessionId, afterSequence)
                .Select(turn => new EllaiCompletedVoiceTurnDto(
                    turn.Sequence,
                    turn.ProviderTurnId,
                    turn.Transcript,
                    sessionId,
                    turn.TurnId.ToString("N"),
                    turn.StreamEpoch))
                .ToArray(),
            snapshot.SessionId));
    }

    private static int ToMilliseconds(TimeSpan duration) =>
        (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue);

    public static void MapEllaiGuestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/v1/ellai/guest");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!HttpMethods.IsGet(http.Request.Method)
                && !HttpMethods.IsHead(http.Request.Method)
                && !HttpMethods.IsOptions(http.Request.Method))
            {
                var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
                try
                {
                    await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
                }
                catch (AntiforgeryValidationException)
                {
                    // A local Docker restart may replace its ephemeral
                    // development key ring. Issue a fresh cookie token on the
                    // rejected request so the client can fetch a matching
                    // request token and retry once without creating a session.
                    antiforgery.GetAndStoreTokens(http);
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "The guest Ellai request could not be validated.");
                }
            }

            if (http.Request.RouteValues.TryGetValue("sessionId", out var routeValue)
                && routeValue is not null)
            {
                var authentication = await http.AuthenticateAsync(AuthenticationScheme).ConfigureAwait(false);
                if (!authentication.Succeeded || authentication.Principal is null
                    || !string.Equals(authentication.Principal.FindFirstValue(GuestClaim), "true", StringComparison.Ordinal))
                {
                    return Results.Unauthorized();
                }

                http.User = authentication.Principal;
                ActorContext actor;
                try
                {
                    actor = ActorContext.FromValidatedPrincipal(http.User);
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Unauthorized();
                }

                var session = await http.RequestServices.GetRequiredService<IEllaiSessionStore>()
                    .GetAsync(routeValue.ToString() ?? string.Empty, http.RequestAborted)
                    .ConfigureAwait(false);
                if (session is null
                    || !string.Equals(session.OwnerActorId, actor.ActorId, StringComparison.Ordinal))
                {
                    return Results.NotFound();
                }
            }

            return await next(context).ConfigureAwait(false);
        });

        group.MapGet("/antiforgery", GetAntiforgeryToken)
            .RequireRateLimiting("ellai-guest-read")
            .WithName("GetEllaiGuestAntiforgeryToken")
            .Produces<EllaiGuestAntiforgeryToken>(StatusCodes.Status200OK);
        group.MapPost("/sessions", StartSessionAsync)
            .RequireRateLimiting("ellai-guest-write")
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("StartEllaiGuestSession")
            .Accepts<StartSessionRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests);
        group.MapPost("/sessions/{sessionId}/consents", GrantPublicHelpConsentAsync)
            .RequireRateLimiting("ellai-guest-write")
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("GrantEllaiGuestPublicHelpConsent")
            .Accepts<GrantEllaiConsentApiRequest>("application/json")
            .Produces<EllaiConsentGrant>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapPost("/sessions/{sessionId}/voice/{mode}", TranscribeVoiceAsync)
            .RequireRateLimiting("ellai-guest-write")
            .WithMetadata(new RequestSizeLimitAttribute(EllaiVoiceTranscriptionService.MaximumAudioBytes))
            .WithName("TranscribeEllaiGuestVoiceCapture")
            .Accepts<byte[]>("audio/wav", "application/octet-stream")
            .Produces<EllaiVoiceTranscriptionResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/sessions/{sessionId}/utterances", SubmitUtteranceAsync)
            .RequireRateLimiting("ellai-guest-write")
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("SubmitEllaiGuestUtterance")
            .Accepts<SubmitUtteranceRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/sessions/{sessionId}/events", ReadEvents)
            .RequireRateLimiting("ellai-guest-read")
            .WithName("PollEllaiGuestSessionEvents")
            .Produces<EllaiSessionEventPageDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/voice/state", ReadVoiceTurnState)
            .RequireRateLimiting("ellai-guest-read")
            .WithName("ReadEllaiGuestVoiceTurnState")
            .Produces<EllaiVoiceTurnStatusDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/responses", ReadResponses)
            .RequireRateLimiting("ellai-guest-read")
            .WithName("ReadEllaiGuestSessionResponses")
            .Produces<EllaiSessionResponsePageDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/responses/{sequence:long}/audio", StreamResponseAudioAsync)
            .RequireRateLimiting("ellai-guest-read")
            .WithName("StreamEllaiGuestResponseAudio")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.swiftaid.pcm")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/sessions/{sessionId}/responses/{sequence:long}/audio/{playbackId:guid}/interrupt",
                InterruptResponseAudioAsync)
            .RequireRateLimiting("ellai-guest-write")
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("InterruptEllaiGuestResponseAudio")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/voice/greeting/audio", StreamListeningGreetingAudioAsync)
            .RequireRateLimiting("ellai-guest-read")
            .WithName("StreamEllaiGuestListeningGreeting")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.swiftaid.pcm")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/sessions/{sessionId}/cancel", CancelSessionAsync)
            .RequireRateLimiting("ellai-guest-write")
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("CancelEllaiGuestSession")
            .Accepts<CancelEllaiSessionApiRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
    }

    private static EllaiGuestAntiforgeryToken GetAntiforgeryToken(
        HttpContext http,
        IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        return new EllaiGuestAntiforgeryToken(tokens.RequestToken ?? string.Empty);
    }

    private static async Task<IResult> StartSessionAsync(
        StartSessionRequest? request,
        HttpContext http,
        EllaiSessionService sessions,
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.Mode != EllaiMode.Ask
            || string.IsNullOrWhiteSpace(request.ClientRequestId)
            || request.ClientRequestId.Length > 120
            || request.FeatureId is not null
            || request.ScenarioId is not null
            || request.InputPreference is not null
            || request.OutputPreference is not null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Guest Ellai supports a fresh public-help session only.");
        }

        var actorId = GuestActorPrefix + Guid.NewGuid().ToString("N");
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "0"),
            new Claim(GuestClaim, "true"),
        ], AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var now = DateTimeOffset.UtcNow;
        var guestScope = new ScopeManifest(
            Array.Empty<ScopeGrantManifest>(), 0, now.AddMinutes(10));

        EllaiSessionView session;
        try
        {
            session = await sessions.StartAsync(request, principal, guestScope, cancellationToken)
                .ConfigureAwait(false);
            await http.SignInAsync(
                AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = false,
                    ExpiresUtc = now.Add(GuestSessionLifetime),
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Guest Ellai is temporarily unavailable.");
        }

        return Results.Json(session, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> GrantPublicHelpConsentAsync(
        string sessionId,
        GrantEllaiConsentApiRequest? request,
        EllaiConsentService consent,
        IEllaiConsentStore consents,
        CancellationToken cancellationToken)
    {
        var voiceConsent = request is not null
            && string.Equals(request.Purpose, EllaiConsentPurposes.VoiceTranscription, StringComparison.Ordinal)
            && Enum.TryParse<EllaiConsentDimension>(request.Dimension, true, out var voiceDimension)
            && voiceDimension is EllaiConsentDimension.Microphone or EllaiConsentDimension.Transcription
            && request.HoursToLive == 1;
        var publicHelpConsent = request is not null
            && string.Equals(request.Dimension, EllaiConsentDimension.ReasoningText.ToString(), StringComparison.Ordinal)
            && string.Equals(request.Purpose, EllaiConsentPurposes.PublicProductHelp, StringComparison.Ordinal)
            && request.HoursToLive == 1;
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || (!voiceConsent && !publicHelpConsent))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Guest Ellai consent request is invalid.");
        }

        var dimension = voiceConsent
            ? Enum.Parse<EllaiConsentDimension>(request.Dimension, true)
            : EllaiConsentDimension.ReasoningText;
        var purpose = voiceConsent
            ? EllaiConsentPurposes.VoiceTranscription
            : EllaiConsentPurposes.PublicProductHelp;
        var dataClass = voiceConsent
            ? EllaiVoiceTranscriptionService.AudioDataClass
            : EllaiProcessingClassification.ConsentedNonSensitive;
        var grant = consent.Grant(sessionId, dimension, purpose, dataClass, TimeSpan.FromHours(1));
        try
        {
            await consents.RecordAsync(grant, cancellationToken).ConfigureAwait(false);
            return Results.Json(grant, statusCode: StatusCodes.Status200OK);
        }
        catch
        {
            consent.Revoke(grant.GrantId);
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Guest Ellai consent could not be saved.");
        }
    }

    private static async Task<IResult> TranscribeVoiceAsync(
        string sessionId,
        string mode,
        HttpRequest request,
        HttpContext http,
        EllaiSessionService sessions,
        EllaiVoiceTranscriptionService transcription,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<EllaiVoiceCaptureMode>(mode, true, out var captureMode)
            || !Enum.IsDefined(captureMode)
            || (captureMode == EllaiVoiceCaptureMode.Recorded
                && !string.Equals(request.ContentType, "audio/wav", StringComparison.OrdinalIgnoreCase))
            || (captureMode == EllaiVoiceCaptureMode.Live
                && !string.Equals(request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The voice capture format is invalid.");
        }

        if (await sessions.GetOwnedViewAsync(sessionId, http.User, cancellationToken).ConfigureAwait(false)
            is not { Status: "Active" })
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The Ellai session is not active.");
        }
        if (!await transcription.HasConsentAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Voice transcription consent is required.");
        }

        var result = await transcription.TranscribeAsync(
            sessionId, captureMode, request.Body, request.ContentLength, cancellationToken).ConfigureAwait(false);
        return result is not null
            ? Results.Json(result)
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Voice transcription is unavailable or the required session consent is missing.");
    }

    private static async Task<IResult> SubmitUtteranceAsync(
        string sessionId,
        SubmitUtteranceRequest? request,
        EllaiUtteranceSubmissionService submissions,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await submissions.SubmitAsync(sessionId, request, http.User, cancellationToken)
            .ConfigureAwait(false);
        return result.Status switch
        {
            EllaiClientOperationStatus.Accepted when result.Session is not null =>
                Results.Json(result.Session, statusCode: StatusCodes.Status200OK),
            EllaiClientOperationStatus.Conflict when result.Session is not null =>
                Results.Json(result.Session, statusCode: StatusCodes.Status409Conflict),
            EllaiClientOperationStatus.Unauthorized => Results.Unauthorized(),
            EllaiClientOperationStatus.InvalidRequest => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The guest Ellai question is invalid."),
            EllaiClientOperationStatus.PolicyBlocked => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "This question is not eligible for public Ellai help."),
            _ => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Guest Ellai could not accept the question."),
        };
    }

    private static IResult ReadEvents(
        string sessionId,
        long cursor,
        EllaiSessionEventJournal journal)
    {
        var page = journal.ReadSince(sessionId, Math.Max(0, cursor));
        return Results.Json(new EllaiSessionEventPageDto(
            page.Events.Select(item => new EllaiSessionEventDto(
                item.Sequence, item.SessionId, item.Kind, item.Ref, item.Detail, item.AtUtc)).ToArray(),
            page.Cursor));
    }

    private static IResult ReadResponses(
        string sessionId,
        long cursor,
        IServiceProvider services)
    {
        var store = services.GetService<EllaiSessionResponseStore>();
        var page = store?.ReadSince(sessionId, Math.Max(0, cursor))
            ?? new EllaiSessionResponsePageDto(Array.Empty<EllaiSessionResponseDto>(), Math.Max(0, cursor));
        return Results.Json(page);
    }

    public static async Task<IResult> StreamResponseAudioAsync(
        string sessionId,
        long sequence,
        HttpContext http,
        EllaiSessionResponseStore responses,
        IEllaiSpeechSynthesisClient speech,
        EllaiResponseSpeechCoordinator speechCoordinator,
        EllaiVoiceTurnCoordinator turnCoordinator,
        Guid? playbackId = null)
    {
        if (sequence < 1 || playbackId == Guid.Empty)
        {
            return Results.NotFound();
        }

        var page = responses.ReadSince(sessionId, sequence - 1, take: 1);
        var response = page.Responses.FirstOrDefault(item => item.Sequence == sequence);
        if (response is null)
        {
            return Results.NotFound();
        }

        if (!speech.IsConfigured)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ellai speech is unavailable for this session.");
        }

        var playback = playbackId is Guid id
            ? speechCoordinator.Begin(sessionId, sequence, id, http.RequestAborted)
            : null;
        if (playbackId is Guid activePlaybackId)
        {
            turnCoordinator.SetAssistantPlayback(sessionId, sequence, activePlaybackId, isPlaying: true);
        }
        var synthesisCancellation = playback?.Token ?? http.RequestAborted;
        HttpResponseMessage upstream;
        try
        {
            upstream = await speech.SynthesizeAsync(
                $"{sessionId}-{sequence}", response.Text, synthesisCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            }
            return Results.Empty;
        }
        catch (OperationCanceledException) when (playback?.Token.IsCancellationRequested == true)
        {
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            }
            return Results.Empty;
        }
        catch (HttpRequestException)
        {
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            }
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ellai speech is temporarily unavailable.");
        }
        catch (InvalidOperationException)
        {
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            }
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ellai speech is temporarily unavailable.");
        }
        catch
        {
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            }
            throw;
        }

        if (!upstream.IsSuccessStatusCode
            || upstream.Content.Headers.ContentType?.MediaType != "application/vnd.swiftaid.pcm")
        {
            upstream.Dispose();
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            }
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ellai speech is temporarily unavailable.");
        }

        CopySpeechHeader(upstream, http.Response, "X-SwiftAid-Voice-Id");
        CopySpeechHeader(upstream, http.Response, "X-SwiftAid-Sample-Rate");
        CopySpeechHeader(upstream, http.Response, "X-SwiftAid-Channels");
        CopySpeechHeader(upstream, http.Response, "X-SwiftAid-Sample-Format");
        http.Response.Headers["X-SwiftAid-Session-Id"] = sessionId;
        http.Response.Headers["X-SwiftAid-Response-Sequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (playbackId is Guid responsePlaybackId)
        {
            http.Response.Headers["X-SwiftAid-Playback-Id"] = responsePlaybackId.ToString("N");
        }
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        Stream audioStream;
        try
        {
            audioStream = await upstream.Content.ReadAsStreamAsync(synthesisCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (synthesisCancellation.IsCancellationRequested)
        {
            upstream.Dispose();
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            }
            return Results.Empty;
        }
        catch
        {
            upstream.Dispose();
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            }
            throw;
        }

        return new EllaiResponseAudioStreamResult(
            audioStream, upstream, playback, turnCoordinator, sessionId, sequence, playbackId);
    }

    public static IResult InterruptResponseAudioAsync(
        string sessionId,
        long sequence,
        Guid playbackId,
        EllaiSessionResponseStore responses,
        EllaiResponseSpeechCoordinator speechCoordinator,
        EllaiVoiceTurnCoordinator turnCoordinator,
        string? turnId = null,
        long? streamEpoch = null)
    {
        if (sequence < 1 || playbackId == Guid.Empty)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai playback interruption was invalid.");
        }

        var response = responses.ReadSince(sessionId, sequence - 1, take: 1)
            .Responses.FirstOrDefault(item => item.Sequence == sequence);
        if (response is null)
        {
            return Results.NotFound();
        }

        var clientConfirmed = false;
        if (!string.IsNullOrWhiteSpace(turnId) || streamEpoch.HasValue)
        {
            if (!Guid.TryParse(turnId, out var parsedTurnId) || streamEpoch is not > 0
                || !turnCoordinator.ConfirmClientBargeIn(
                    sessionId, streamEpoch.Value, sequence, playbackId, parsedTurnId))
            {
                return Results.Conflict();
            }

            clientConfirmed = true;
        }

        // A local client detector can stop playback promptly, but it is only
        // a candidate. Gateway Silero VAD remains responsible for confirming
        // speech and cancelling server-side response generation.
        var playbackInterrupted = false;
        if (!clientConfirmed)
        {
            turnCoordinator.InterruptPlayback(sessionId, sequence, playbackId);
            playbackInterrupted = speechCoordinator.Interrupt(sessionId, sequence, playbackId);
        }
        return playbackInterrupted || clientConfirmed
            ? Results.NoContent()
            : Results.Conflict();
    }

    internal static async Task<IResult> StreamListeningGreetingAudioAsync(
        string sessionId,
        HttpContext http,
        IEllaiSessionStore sessions,
        IEllaiSpeechSynthesisClient speech)
    {
        var session = await sessions.GetAsync(sessionId, http.RequestAborted).ConfigureAwait(false);
        if (session is null || !string.Equals(session.Status, "Active", StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        if (!speech.IsConfigured)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ellai speech is unavailable for this session.");
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await speech.SynthesizeAsync(
                $"{sessionId}-live-activation", "Hi, What's on your mind!",
                http.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ellai speech is temporarily unavailable.");
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ellai speech is temporarily unavailable.");
        }

        if (!upstream.IsSuccessStatusCode
            || upstream.Content.Headers.ContentType?.MediaType != "application/vnd.swiftaid.pcm")
        {
            upstream.Dispose();
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ellai speech is temporarily unavailable.");
        }

        CopySpeechHeader(upstream, http.Response, "X-SwiftAid-Voice-Id");
        CopySpeechHeader(upstream, http.Response, "X-SwiftAid-Sample-Rate");
        CopySpeechHeader(upstream, http.Response, "X-SwiftAid-Channels");
        CopySpeechHeader(upstream, http.Response, "X-SwiftAid-Sample-Format");
        http.Response.Headers["X-SwiftAid-Session-Id"] = sessionId;
        http.Response.Headers["X-SwiftAid-Response-Sequence"] = "0";
        http.Response.Headers["X-SwiftAid-Speech-Purpose"] = "live-activation";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var audioStream = await upstream.Content.ReadAsStreamAsync(http.RequestAborted).ConfigureAwait(false);
        http.Response.OnCompleted(() =>
        {
            upstream.Dispose();
            return Task.CompletedTask;
        });
        return Results.Stream(audioStream, contentType: "application/vnd.swiftaid.pcm");
    }

    private static void CopySpeechHeader(HttpResponseMessage upstream, HttpResponse response, string name)
    {
        if (upstream.Headers.TryGetValues(name, out var values))
        {
            response.Headers[name] = values.ToArray();
        }
    }

    private static async Task<IResult> CancelSessionAsync(
        string sessionId,
        CancelEllaiSessionApiRequest? request,
        EllaiSessionService sessions,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (request is null || request.ExpectedSessionVersion < 1)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The guest Ellai cancellation is invalid.");
        }

        var cancelled = await sessions.CancelAsync(
            sessionId, request.ExpectedSessionVersion, http.User, cancellationToken).ConfigureAwait(false);
        if (!cancelled)
        {
            var current = await sessions.GetOwnedViewAsync(sessionId, http.User, cancellationToken)
                .ConfigureAwait(false);
            return current is null
                ? Results.NotFound()
                : Results.Json(current, statusCode: StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;
        EllaiSessionCancellationEffects.Apply(
            sessionId,
            now,
            http.RequestServices.GetService<EllaiApprovalDialogCoordinator>(),
            http.RequestServices.GetService<EllaiSessionEventJournal>(),
            http.RequestServices.GetService<EllaiSessionResponseStore>());
        await http.SignOutAsync(AuthenticationScheme).ConfigureAwait(false);
        return Results.Ok(await sessions.GetOwnedViewAsync(sessionId, http.User, cancellationToken)
            .ConfigureAwait(false));
    }
}

internal sealed class EllaiResponseAudioStreamResult(
    Stream audioStream,
    HttpResponseMessage upstream,
    EllaiResponseSpeechCoordinator.PlaybackLease? playback,
    EllaiVoiceTurnCoordinator turnCoordinator,
    string sessionId,
    long sequence,
    Guid? playbackId) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "application/vnd.swiftaid.pcm";
        try
        {
            using (playback)
            using (upstream)
            using (audioStream)
            using (var cancellation = playback is null
                ? CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted)
                : CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted, playback.Token))
            {
                try
                {
                    await audioStream.CopyToAsync(httpContext.Response.Body, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // An explicit playback stop or disconnected client ends only this response stream.
                }
            }
        }
        finally
        {
            if (playbackId is Guid id)
            {
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, id, isPlaying: false);
            }
        }
    }
}

public sealed record EllaiGuestAntiforgeryToken(string RequestToken);
