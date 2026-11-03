using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Sync;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Public Ellai surface for anonymous guests. The signed guest
/// cookie binds each request to one opaque actor; guest sessions have no
/// workspace grants; tools are restricted to public facts and non-writing capture assistance.</summary>
public static class EllaiGuestEndpoints
{
    public const string AuthenticationScheme = "SwiftAid.EllaiGuest";
    public const string GuestClaim = "swiftaid_ellai_guest";
    internal static readonly object AudioSessionFenceKey = new();
    private const string GuestActorPrefix = "ellai-guest-";

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
                .TakeWhile(turn => !turn.ServerSubmission || turn.Admission is not null)
                .Select(turn => new EllaiCompletedVoiceTurnDto(
                    turn.Sequence,
                    turn.ProviderTurnId,
                    turn.Transcript,
                    sessionId,
                    turn.TurnId.ToString("N"),
                    turn.StreamEpoch,
                    AudioStartSample: turn.AudioStartSample,
                    AudioEndSample: turn.AudioEndSample,
                    VoiceCaptureId: turn.VoiceCaptureId,
                    ServerSubmission: turn.ServerSubmission,
                    Admission: turn.Admission))
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

        group.MapGet("/sessions/{sessionId}/guidance/admission", EllaiGuidanceProposalEndpoint.ReadAsync);
        group.MapPost("/sessions/{sessionId}/guidance/plan", EllaiGuidanceProposalEndpoint.PrepareAsync);
        group.MapPost("/sessions/{sessionId}/guidance/narration", EllaiGuidanceProposalEndpoint.NarrateAsync);
        group.MapPost("/sessions/{sessionId}/guidance/validation-narration", EllaiGuidanceProposalEndpoint.NarrateValidationAsync);
        group.MapPost("/sessions/{sessionId}/guidance/steps/complete", EllaiGuidanceProposalEndpoint.CompleteStepAsync);
        group.MapPost("/sessions/{sessionId}/guidance/control", EllaiGuidanceProposalEndpoint.ControlAsync);
        group.MapPost("/sessions/{sessionId}/guidance/receipt/complete", EllaiGuidanceProposalEndpoint.CompleteReceiptStepAsync)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096));

        group.MapPost("/sessions/{sessionId}/ui-observations", EllaiEndpoints.ReportUiObservationAsync)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024));

        group.MapGet("/antiforgery", GetAntiforgeryToken)
            .WithName("GetEllaiGuestAntiforgeryToken")
            .Produces<EllaiGuestAntiforgeryToken>(StatusCodes.Status200OK);
        group.MapGet("/sessions", async (HttpContext http, [FromServices] IEllaiSessionStore sessions,
            CancellationToken cancellationToken, int offset = 0) =>
        {
            var authentication = await http.AuthenticateAsync(AuthenticationScheme).ConfigureAwait(false);
            if (!authentication.Succeeded || authentication.Principal?.FindFirstValue(GuestClaim) != "true")
            {
                http.Response.Headers.CacheControl = "no-store, private";
                return Results.Json(new EllaiSessionHistoryPageDto([], null));
            }
            http.User = authentication.Principal;
            return await EllaiConversationFeed.ListSessionsAsync(http, sessions, cancellationToken, offset).ConfigureAwait(false);
        });
        group.MapPost("/sessions", StartSessionAsync)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("StartEllaiGuestSession")
            .Accepts<StartSessionRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);
        group.MapPost("/sessions/{sessionId}/consents", GrantPublicHelpConsentAsync)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("GrantEllaiGuestPublicHelpConsent")
            .Accepts<GrantEllaiConsentApiRequest>("application/json")
            .Produces<EllaiConsentGrant>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapPost("/sessions/{sessionId}/voice/{mode:regex(^(recorded|recover)$)}", TranscribeVoiceAsync)
            .WithMetadata(new RequestSizeLimitAttribute(EllaiVoiceTranscriptionService.MaximumAudioBytes))
            .WithName("TranscribeEllaiGuestVoiceCapture")
            .Accepts<byte[]>("audio/wav", "application/octet-stream")
            .Produces<EllaiVoiceTranscriptionResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/sessions/{sessionId}/utterances", SubmitUtteranceAsync)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("SubmitEllaiGuestUtterance")
            .Accepts<SubmitUtteranceRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/sessions/{sessionId}/events", ReadEvents)
            .WithName("PollEllaiGuestSessionEvents")
            .Produces<EllaiSessionEventPageDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/voice/state", ReadVoiceTurnState)
            .WithName("ReadEllaiGuestVoiceTurnState")
            .Produces<EllaiVoiceTurnStatusDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/voice/live-stream", EllaiVoiceWebSocketEndpoint.HandleAsync)
            .WithName("StreamEllaiGuestLiveVoiceWebSocket")
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}", EllaiConversationFeed.ReadSessionAsync);

        group.MapGet("/sessions/{sessionId}/responses", EllaiConversationFeed.ReadAsync)
            .WithName("ReadEllaiGuestSessionResponses")
            .Produces<EllaiSessionResponsePageDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/responses/{sequence:long}/audio", StreamResponseAudioAsync)
            .AddEndpointFilter(RequireUncancelledAudioSession)
            .WithName("StreamEllaiGuestResponseAudio")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.swiftaid.pcm")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/sessions/{sessionId}/responses/{sequence:long}/audio/{playbackId:guid}/interrupt",
                InterruptResponseAudioAsync)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("InterruptEllaiGuestResponseAudio")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/voice/greeting/audio", StreamListeningGreetingAudioAsync)
            .WithName("StreamEllaiGuestListeningGreeting")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.swiftaid.pcm")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/sessions/{sessionId}/cancel", CancelSessionAsync)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("CancelEllaiGuestSession")
            .Accepts<CancelEllaiSessionApiRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
    }

    internal static async ValueTask<object?> RequireUncancelledAudioSession(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var sessionId = http.Request.RouteValues["sessionId"]?.ToString();
        if (string.IsNullOrWhiteSpace(sessionId)) return Results.NotFound();
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(http.User); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        var sessions = http.RequestServices.GetRequiredService<IEllaiSessionStore>();
        var session = await sessions.GetAsync(sessionId, http.RequestAborted).ConfigureAwait(false);
        if (session is null || session.Status == "Cancelled" || session.OwnerActorId != actor.ActorId)
            return Results.NotFound();
        http.Items[AudioSessionFenceKey] = (Func<CancellationToken, Task<bool>>)(async ct =>
        {
            var current = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return current is not null && current.Status != "Cancelled" && current.OwnerActorId == actor.ActorId &&
                current.InteractionEpoch == session.InteractionEpoch && current.ScopeEpoch == session.ScopeEpoch;
        });
        return await next(context).ConfigureAwait(false);
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
        var authentication = await http.AuthenticateAsync(AuthenticationScheme).ConfigureAwait(false);
        var principal = authentication.Succeeded
            && authentication.Principal?.FindFirstValue(GuestClaim) == "true"
            && authentication.Principal.FindFirstValue(ClaimTypes.NameIdentifier)?.StartsWith(GuestActorPrefix, StringComparison.Ordinal) == true
                ? authentication.Principal
                : new ClaimsPrincipal(identity);
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
        var captureMode = mode == "recover" ? EllaiVoiceCaptureMode.Live : EllaiVoiceCaptureMode.Recorded;
        if (request.ContentLength is not (> 0 and <= EllaiVoiceTranscriptionService.MaximumAudioBytes)
            || !string.Equals(request.ContentType, captureMode == EllaiVoiceCaptureMode.Recorded ? "audio/wav" : "application/octet-stream", StringComparison.OrdinalIgnoreCase))
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

        var preferences = EllaiSpeechPreferences.Read(request.Query);
        if (preferences is null) return Results.BadRequest();
        var result = await transcription.TranscribeAsync(
            sessionId, captureMode, request.Body, request.ContentLength, cancellationToken,
            request.Query["voiceCaptureId"].FirstOrDefault(),
            long.TryParse(request.Query["voiceCaptureOffsetSamples"].FirstOrDefault(), out var offsetSamples)
                ? offsetSamples : 0,
            submissionPrincipal: request.Query["serverSubmission"] == "true" ? http.User : null,
            speechPreferences: preferences)
            .ConfigureAwait(false);
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


    public static async Task<IResult> StreamResponseAudioAsync(
        string sessionId,
        long sequence,
        HttpContext http,
        EllaiSessionResponseStore responses,
        [FromServices] EllaiPreparedResponseAudio preparedAudio,
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
        if (response is null || string.IsNullOrWhiteSpace(response.Text)
            || (response.Progress is not null && !responses.CanSpeakProgress(response)))
        {
            return Results.NotFound();
        }

        if (!responses.TryGetProvisionalState(sessionId, sequence, out var provisional)) return Results.NotFound();
        if (provisional)
        {
            var actor = ActorContext.FromValidatedPrincipal(http.User);
            var sessions = http.RequestServices.GetRequiredService<IEllaiSessionStore>();
            var outbox = http.RequestServices.GetRequiredService<IEllaiUtteranceOutboxStore>();
            var current = await sessions.GetAsync(sessionId, http.RequestAborted).ConfigureAwait(false);
            if (current is null || current.Status != "Active" || current.OwnerActorId != actor.ActorId)
                return Results.NotFound();
            var conversation = await outbox.ReadConversationAsync(sessionId, actor.ActorId, http.RequestAborted).ConfigureAwait(false);
            if (!responses.CanDeliver(sessionId, response, current.ScopeEpoch, current.InteractionEpoch, conversation, DateTimeOffset.UtcNow))
                return Results.NotFound();
            var sessionFence = http.Items[AudioSessionFenceKey] as Func<CancellationToken, Task<bool>>;
            http.Items[AudioSessionFenceKey] = (Func<CancellationToken, Task<bool>>)(async ct =>
            {
                if (sessionFence is not null && !await sessionFence(ct).ConfigureAwait(false)) return false;
                var latest = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
                if (latest is null || latest.Status != "Active" || latest.OwnerActorId != actor.ActorId) return false;
                var turns = await outbox.ReadConversationAsync(sessionId, actor.ActorId, ct).ConfigureAwait(false);
                return responses.CanDeliver(sessionId, response, latest.ScopeEpoch, latest.InteractionEpoch, turns, DateTimeOffset.UtcNow);
            });
        }

        if (response.Language is not ("eng" or "sna" or "nya" or "tso"))
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
            upstream = await preparedAudio.OpenAsync(sessionId, sequence, synthesisCancellation).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            playback?.Dispose();
            if (playbackId is Guid stoppedPlaybackId)
                turnCoordinator.SetAssistantPlayback(sessionId, sequence, stoppedPlaybackId, isPlaying: false);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Ellai speech is temporarily unavailable.");
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

        if (!EllaiSpeechResponseContract.IsValidPcmResponse(upstream))
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
            audioStream, upstream, playback, turnCoordinator, preparedAudio, sessionId, sequence, playbackId);
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

        var preferences = EllaiSpeechPreferences.Read(http.Request.Query);
        if (preferences is null) return Results.BadRequest();
        HttpResponseMessage upstream;
        try
        {
            upstream = await speech.SynthesizeAsync(
                $"{sessionId}-live-activation", "Hi, What's on your mind!",
                "eng", preferences.EnglishVoiceId, http.RequestAborted).ConfigureAwait(false);
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
        // Ending one conversation does not end the guest identity that owns its history.
        return Results.Ok(await sessions.GetOwnedViewAsync(sessionId, http.User, cancellationToken)
            .ConfigureAwait(false));
    }
}

internal sealed class EllaiResponseAudioStreamResult(
    Stream audioStream,
    HttpResponseMessage upstream,
    EllaiResponseSpeechCoordinator.PlaybackLease? playback,
    EllaiVoiceTurnCoordinator turnCoordinator,
    EllaiPreparedResponseAudio preparedAudio,
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
                var fence = httpContext.Items[EllaiGuestEndpoints.AudioSessionFenceKey] as Func<CancellationToken, Task<bool>>;
                var invalidated = 0;
                if (fence is not null)
                {
                    try
                    {
                        if (!await fence(cancellation.Token).ConfigureAwait(false)) invalidated = 1;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                    catch (Exception error) when (error is not OutOfMemoryException) { invalidated = 1; }
                    if (invalidated != 0)
                    {
                        preparedAudio.Interrupt(sessionId, sequence);
                        httpContext.Abort();
                        return;
                    }
                }
                async Task MonitorSessionAsync()
                {
                    try
                    {
                        while (true)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token).ConfigureAwait(false);
                            if (!await fence!(cancellation.Token).ConfigureAwait(false)) break;
                        }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    Interlocked.Exchange(ref invalidated, 1);
                    preparedAudio.Interrupt(sessionId, sequence);
                    await cancellation.CancelAsync().ConfigureAwait(false);
                }
                var monitor = fence is null ? Task.CompletedTask : MonitorSessionAsync();
                try
                {
                    await audioStream.CopyToAsync(httpContext.Response.Body, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // An explicit playback stop or disconnected client ends only this response stream.
                }
                catch (IOException)
                {
                    // Do not turn a truncated/invalidated prepared response into a successful PCM EOF.
                    httpContext.Abort();
                }
                finally
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                    await monitor.ConfigureAwait(false);
                    if (Volatile.Read(ref invalidated) != 0) httpContext.Abort();
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
