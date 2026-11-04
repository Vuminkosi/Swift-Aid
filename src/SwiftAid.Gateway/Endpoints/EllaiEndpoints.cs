using Microsoft.AspNetCore.Mvc;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Marks routes that expose approval state, receipts, or privileged
/// plan commands and therefore require a trusted scoped-staff capability.</summary>
public sealed record EllaiScopedAuthorizationRequiredMetadata;

/// <summary>Authenticated Ellai session endpoints (ELL-T04/T08 groundwork).
/// Stale expected versions fail with 409; terminal-state or budget violations
/// fail with 422; denial is a generic 404 that never reveals session existence.
/// Wire DTOs never accept trusted authorization fields; actor/scope stamps are
/// server-attached inside the application service.</summary>
public static class EllaiEndpoints
{
    private const int MaximumRequestBodyBytes = 64 * 1024;

    public static void MapEllaiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Public inventory contains installed English voice labels only, never session data.
        endpoints.MapGet("/api/v1/ellai/voices", async (IEllaiSpeechSynthesisClient speech, CancellationToken ct) =>
            Results.Json(await speech.ListEnglishVoicesAsync(ct))).AllowAnonymous();

        var group = endpoints.MapGroup("/api/v1/ellai");
        group.RequireAuthorization("SwiftAidStaffApi");
        group.AddEndpointFilter(async (context, next) =>
        {
            // No current trusted scoped-staff capability is wired for these
            // privileged plan/evidence surfaces. Authentication and session
            // ownership alone do not grant access to them.
            if (context.HttpContext.GetEndpoint()?.Metadata
                .GetMetadata<EllaiScopedAuthorizationRequiredMetadata>() is not null)
            {
                return Results.NotFound();
            }

            if (context.HttpContext.Request.RouteValues.TryGetValue("sessionId", out var routeValue)
                && routeValue is not null)
            {
                var principal = context.HttpContext.User;
                ActorContext actor;
                try
                {
                    actor = ActorContext.FromValidatedPrincipal(principal);
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Unauthorized();
                }

                var sessionStore = context.HttpContext.RequestServices
                    .GetRequiredService<IEllaiSessionStore>();
                var session = await sessionStore.GetAsync(
                    routeValue.ToString() ?? string.Empty,
                    context.HttpContext.RequestAborted);
                if (session is null
                    || !string.Equals(session.OwnerActorId, actor.ActorId, StringComparison.Ordinal))
                {
                    return Results.NotFound();
                }
            }

            return await next(context);
        });
        group.MapGet("/sessions", EllaiConversationFeed.ListSessionsAsync);
        group.MapPost("/sessions/{sessionId}/report-voice/consent", ReportVoiceEndpoints.ConsentAsync)
            .WithMetadata(new RequestSizeLimitAttribute(1024));
        group.MapPost("/sessions/{sessionId}/report-voice/extract", ReportVoiceEndpoints.ExtractAsync)
            .WithMetadata(new RequestSizeLimitAttribute(32768));
        group.MapPost("/sessions/{sessionId}/report-voice/saved-recording", ReportVoiceEndpoints.TranscribeSavedAsync)
            .WithMetadata(new RequestSizeLimitAttribute(EllaiVoiceTranscriptionService.MaximumAudioBytes));
        group.MapPost("/sessions", StartAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("StartEllaiSession")
            .Accepts<StartSessionRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
        group.MapPost("/sessions/{sessionId}/utterances", SubmitUtteranceAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("SubmitEllaiUtterance")
            .Accepts<SubmitUtteranceRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/sessions/{sessionId}/approvals", SubmitApprovalAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("SubmitEllaiApproval")
            .WithMetadata(new EllaiScopedAuthorizationRequiredMetadata())
            .Accepts<SubmitApprovalApiRequest>("application/json")
            .Produces<EllaiReceipt>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/sessions/{sessionId}/events", PollEventsAsync)
            .WithName("PollEllaiSessionEvents")
            .Produces<EllaiSessionEventPage>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/voice/state", EllaiGuestEndpoints.ReadVoiceTurnState)
            .WithName("ReadEllaiVoiceTurnState")
            .Produces<EllaiVoiceTurnStatusDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/voice/live-stream", EllaiVoiceWebSocketEndpoint.HandleAsync)
            .WithName("StreamEllaiLiveVoiceWebSocket")
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}", EllaiConversationFeed.ReadSessionAsync);

        group.MapGet("/sessions/{sessionId}/responses", EllaiConversationFeed.ReadAsync)
            .WithName("ReadEllaiSessionResponses")
            .Produces<EllaiSessionResponsePageDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/responses/{sequence:long}/audio",
                EllaiGuestEndpoints.StreamResponseAudioAsync)
            .AddEndpointFilter(EllaiGuestEndpoints.RequireUncancelledAudioSession)
            .WithName("StreamEllaiResponseAudio")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.swiftaid.pcm")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/sessions/{sessionId}/responses/{sequence:long}/audio/{playbackId:guid}/interrupt",
                EllaiGuestEndpoints.InterruptResponseAudioAsync)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
            .WithName("InterruptEllaiResponseAudio")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/voice/greeting/audio",
                EllaiGuestEndpoints.StreamListeningGreetingAudioAsync)
            .WithName("StreamEllaiListeningGreeting")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.swiftaid.pcm")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/sessions/{sessionId}/receipts", ListReceipts)
            .WithName("ListEllaiReceipts")
            .WithMetadata(new EllaiScopedAuthorizationRequiredMetadata())
            .Produces<IReadOnlyList<EllaiApprovedReceiptDto>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapPost("/sessions/{sessionId}/approval-reviews", RegisterReviewAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("RegisterEllaiApprovalReview")
            .WithMetadata(new EllaiScopedAuthorizationRequiredMetadata())
            .Accepts<RegisterApprovalReviewApiRequest>("application/json")
            .Produces<EllaiApprovalDialogRequest>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
        group.MapGet("/sessions/{sessionId}/approval-reviews/active", GetActiveReviewAsync)
            .WithName("GetEllaiActiveApprovalReview")
            .WithMetadata(new EllaiScopedAuthorizationRequiredMetadata())
            .Produces<EllaiApprovalDialogRequest>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapPost("/sessions/{sessionId}/approval-reviews/{requestId}/edit", EditReviewAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("EditEllaiApprovalReview")
            .WithMetadata(new EllaiScopedAuthorizationRequiredMetadata())
            .Accepts<EditApprovalReviewApiRequest>("application/json")
            .Produces<EllaiApprovalDialogRequest>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        group.MapPost("/sessions/{sessionId}/approval-reviews/{requestId}/approve", ApproveSelectedAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("ApproveEllaiSelectedActions")
            .WithMetadata(new EllaiScopedAuthorizationRequiredMetadata())
            .Accepts<ApproveSelectedApiRequest>("application/json")
            .Produces<IReadOnlyList<EllaiApprovedReceiptDto>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/sessions/{sessionId}/approval-reviews/{requestId}/reject", RejectReviewAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("RejectEllaiApprovalReview")
            .WithMetadata(new EllaiScopedAuthorizationRequiredMetadata())
            .Accepts<RejectReviewApiRequest>("application/json")
            .Produces<bool>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        group.MapGet("/sessions/{sessionId}/guidance/admission", EllaiGuidanceProposalEndpoint.ReadAsync);
        group.MapPost("/sessions/{sessionId}/guidance/plan", EllaiGuidanceProposalEndpoint.PrepareAsync);
        group.MapPost("/sessions/{sessionId}/guidance/narration", EllaiGuidanceProposalEndpoint.NarrateAsync);
        group.MapPost("/sessions/{sessionId}/guidance/validation-narration", EllaiGuidanceProposalEndpoint.NarrateValidationAsync);
        group.MapPost("/sessions/{sessionId}/guidance/steps/complete", EllaiGuidanceProposalEndpoint.CompleteStepAsync);
        group.MapPost("/sessions/{sessionId}/guidance/control", EllaiGuidanceProposalEndpoint.ControlAsync);
        group.MapPost("/sessions/{sessionId}/guidance/receipt/complete", EllaiGuidanceProposalEndpoint.CompleteReceiptStepAsync)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096));

        group.MapPost("/sessions/{sessionId}/ui-observations", ReportUiObservationAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("ReportEllaiUiObservation")
            .Accepts<ReportUiObservationApiRequest>("application/json")
            .Produces<bool>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
        group.MapPost("/sessions/{sessionId}/consents", GrantConsentAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("GrantEllaiConsent")
            .Accepts<GrantEllaiConsentApiRequest>("application/json")
            .Produces<EllaiConsentGrant>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
        group.MapPost("/sessions/{sessionId}/voice/{mode:regex(^(recorded|recover)$)}", TranscribeVoiceAsync)
            .WithMetadata(new RequestSizeLimitAttribute(EllaiVoiceTranscriptionService.MaximumAudioBytes))
            .WithName("TranscribeEllaiVoiceCapture")
            .Accepts<byte[]>("audio/wav", "application/octet-stream")
            .Produces<EllaiVoiceTranscriptionResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
        group.MapDelete("/sessions/{sessionId}/consents/{grantId}", RevokeConsentAsync)
            .WithName("RevokeEllaiConsent")
            .Produces<bool>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapPost("/sessions/{sessionId}/cancel", CancelAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("CancelEllaiSession")
            .Accepts<CancelEllaiSessionApiRequest>("application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> StartAsync(
        StartSessionRequest? request,
        EllaiSessionService sessions,
        EllaiSessionScopeResolver scopeResolver,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai session request is invalid.");
        }

        if (request.FinalizedBoundsCheckFails())
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai session request is invalid.");
        }

        ActorContext actor;
        try
        {
            actor = ActorContext.FromValidatedPrincipal(http.User);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }

        try
        {
            var scope = await scopeResolver.GetAsync(actor, cancellationToken).ConfigureAwait(false);
            var view = await sessions.StartAsync(request, http.User, scope, cancellationToken).ConfigureAwait(false);
            return Results.Json(view, statusCode: StatusCodes.Status200OK);
        }
        catch (ScopeUnavailableException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "A current SwiftAid access scope is required to start Ellai.");
        }
    }

    private static async Task<IResult> SubmitUtteranceAsync(
        string sessionId,
        SubmitUtteranceRequest? request,
        EllaiUtteranceSubmissionService submissions,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await submissions.SubmitAsync(
            sessionId, request, http.User, cancellationToken);
        return result.Status switch
        {
            EllaiClientOperationStatus.Unauthorized => Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The Ellai utterance needs an authenticated actor."),
            EllaiClientOperationStatus.InvalidRequest => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai utterance is invalid."),
            EllaiClientOperationStatus.Conflict when result.Session is not null =>
                Results.Json(result.Session, statusCode: StatusCodes.Status409Conflict),
            EllaiClientOperationStatus.Conflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The Ellai session changed before the utterance was recorded."),
            EllaiClientOperationStatus.PolicyBlocked => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "This Ellai request cannot be processed under the current data policy."),
            EllaiClientOperationStatus.Accepted when result.Session is not null =>
                Results.Json(result.Session, statusCode: StatusCodes.Status200OK),
            _ => Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The Ellai utterance could not be recorded."),
        };
    }

    private static async Task<IResult> SubmitApprovalAsync(
        string sessionId,
        SubmitApprovalApiRequest? request,
        EllaiApprovalService approvals,
        EllaiApprovalDialogCoordinator dialogs,
        EllaiSessionEventJournal journals,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.PlanHash)
            || string.IsNullOrWhiteSpace(request.ApprovedPlanHash)
            || request.Proposal is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai approval request is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.ApprovalRequestId))
        {
            var review = dialogs.TryGetActive(sessionId, now);
            if (review is null
                || !string.Equals(review.RequestId, request.ApprovalRequestId, StringComparison.Ordinal)
                || review.State != EllaiApprovalDialogState.AwaitingApproval
                || !string.Equals(review.PlanHash, request.PlanHash, StringComparison.Ordinal)
                || review.PlanVersion != request.PlanVersion)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The approval review is stale, superseded or already closed.");
            }
        }

        var receipt = await approvals.SubmitApprovalAsync(
            sessionId, request.PlanHash, request.PlanVersion, request.Proposal,
            request.ApprovedPlanHash, request.ApprovedPlanVersion,
            request.ApprovedRecipient, request.ApprovedDueAtUtc, now,
            cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.ApprovalRequestId))
        {
            var resolved = dialogs.Resolve(request.ApprovalRequestId, receipt is not null, now);
            if (resolved is not null)
            {
                journals.Append(sessionId, "approval-resolved", resolved.RequestId,
                    resolved.State.ToString(), now);
            }
        }

        if (receipt is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The approval does not exactly match the proposed plan.");
        }

        return Results.Json(receipt, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> ListReceipts(
        string sessionId,
        IEllaiApprovalStore receipts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai receipt request is invalid.");
        }

        var list = await receipts.ListBySessionAsync(sessionId, cancellationToken);
        return Results.Json(MapReceipts(list), statusCode: StatusCodes.Status200OK);
    }

    /// <summary>Wire projection for durable receipts: identity plus the
    /// status name. Statuses serialize as names (never enum numbers) so
    /// typed clients can parse them without guessing the lattice.</summary>
    private static IReadOnlyList<EllaiApprovedReceiptDto> MapReceipts(
        IReadOnlyList<EllaiReceipt> receipts) => receipts
        .Select(receipt => new EllaiApprovedReceiptDto(
            receipt.ReceiptId, receipt.ActionId, receipt.Status.ToString()))
        .ToList();

    private static async Task<IResult> CancelAsync(
        string sessionId,
        CancelEllaiSessionApiRequest? request,
        EllaiSessionService sessions,
        EllaiApprovalDialogCoordinator dialogs,
        EllaiSessionEventJournal journals,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai cancel request is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        var cancelled = await sessions.CancelAsync(
            sessionId, request.ExpectedSessionVersion, http.User, cancellationToken);
        if (!cancelled)
        {
            var current = await sessions.GetOwnedViewAsync(sessionId, http.User, cancellationToken);
            if (current is not null)
            {
                return Results.Json(current, statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The Ellai session changed before cancellation was recorded.");
        }

        EllaiSessionCancellationEffects.Apply(
            sessionId,
            now,
            dialogs,
            journals,
            http.RequestServices.GetService<EllaiSessionResponseStore>());

        return Results.Ok();
    }

    private static Task<IResult> PollEventsAsync(
        string sessionId,
        long? cursor,
        int? waitSeconds,
        EllaiSessionEventJournal journals,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai event request is invalid."));
        }

        return PollEventsLoopAsync(sessionId, Math.Max(0, cursor ?? 0),
            Math.Clamp(waitSeconds ?? 20, 0, 30), journals, cancellationToken);
    }


    private static async Task<IResult> PollEventsLoopAsync(
        string sessionId, long position, int waitSeconds,
        EllaiSessionEventJournal journals, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            var page = journals.ReadSince(sessionId, position);
            if (page.Events.Count > 0 || DateTimeOffset.UtcNow >= deadline)
            {
                return Results.Json(page, statusCode: StatusCodes.Status200OK);
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new EllaiSessionEventPage([], position),
                    statusCode: StatusCodes.Status200OK);
            }
        }
    }

    private static Task<IResult> RegisterReviewAsync(
        string sessionId,
        RegisterApprovalReviewApiRequest? request,
        EllaiApprovalDialogCoordinator dialogs,
        EllaiSessionEventJournal journals,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.RequestId)
            || string.IsNullOrWhiteSpace(request.PlanHash))
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai approval review request is invalid."));
        }

        if (request.Proposals is not null && request.Proposals.Count > EllaiDecisionValidator.MaxPlanActions)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A plan carries at most three administrative actions."));
        }

        var now = DateTimeOffset.UtcNow;
        var (review, _) = dialogs.Register(sessionId, request.RequestId, request.PlanHash,
            request.PlanVersion, now, EllaiApprovalService.ApprovalLifetime, request.Proposals);
        journals.Append(sessionId, "approval-required", review.RequestId,
            $"{review.PlanHash} v{review.PlanVersion} · {review.State}", now);
        return Task.FromResult(Results.Json(review, statusCode: StatusCodes.Status200OK));
    }

    private static Task<IResult> GetActiveReviewAsync(
        string sessionId,
        EllaiApprovalDialogCoordinator dialogs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai review request is invalid."));
        }

        var active = dialogs.TryGetActive(sessionId, DateTimeOffset.UtcNow);
        if (active is null)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "There is no open approval review for this session."));
        }

        return Task.FromResult(Results.Json(active, statusCode: StatusCodes.Status200OK));
    }

    private static Task<IResult> EditReviewAsync(
        string sessionId,
        string requestId,
        EditApprovalReviewApiRequest? request,
        EllaiApprovalDialogCoordinator dialogs,
        EllaiSessionEventJournal journals,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.NewRequestId)
            || string.IsNullOrWhiteSpace(request.NewPlanHash))
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai approval edit request is invalid."));
        }

        if (request.Proposals is not null && request.Proposals.Count > EllaiDecisionValidator.MaxPlanActions)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A plan carries at most three administrative actions."));
        }

        var now = DateTimeOffset.UtcNow;
        var next = dialogs.ApplyEdit(requestId, request.NewRequestId, request.NewPlanHash,
            request.NewPlanVersion, now, EllaiApprovalService.ApprovalLifetime, request.Proposals);
        if (next is null)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The approval review is stale, superseded or already closed."));
        }

        journals.Append(sessionId, "approval-edit", next.RequestId,
            $"{next.PlanHash} v{next.PlanVersion} · {next.State}", now);
        return Task.FromResult(Results.Json(next, statusCode: StatusCodes.Status200OK));
    }

    private static async Task<IResult> ApproveSelectedAsync(
        string sessionId,
        string requestId,
        ApproveSelectedApiRequest? request,
        EllaiApprovalService approvals,
        EllaiApprovalDialogCoordinator dialogs,
        EllaiSessionEventJournal journals,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || request.ActionIds is null
            || request.ActionIds.Count == 0
            || request.ActionIds.Count > EllaiDecisionValidator.MaxPlanActions)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai approval selection is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        var review = dialogs.Get(requestId);
        if (review is null
            || !string.Equals(review.SessionId, sessionId, StringComparison.Ordinal)
            || review.State != EllaiApprovalDialogState.AwaitingApproval)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The approval review is stale, superseded or already closed.");
        }

        var receipts = await approvals.ApproveReviewedAsync(
            review, request.ActionIds, now, cancellationToken: cancellationToken);
        if (receipts is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The selection does not match the reviewed exact plan.");
        }

        dialogs.Resolve(requestId, approved: true, now);
        journals.Append(sessionId, "approval-resolved", requestId, "Approved", now);
        return Results.Json(MapReceipts(receipts), statusCode: StatusCodes.Status200OK);
    }

    internal static Task<IResult> ReportUiObservationAsync(
        string sessionId,
        ReportUiObservationApiRequest? request,
        IEllaiUiObservationStore observations,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || !(new UiObservationReport(request.ViewInstanceId, request.ContextStamp,
                request.Platform, request.MountedControlIds, request.ControlStates, request.CurrentNavigationId, request.CompletedControlIds)
                { ValidationInputIds = request.ValidationInputIds, InputStates = request.InputStates }).IsValid())
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai UI observation report is invalid."));
        }

        return RecordUiObservationAsync(sessionId, request, observations, cancellationToken);
    }

    private static async Task<IResult> RecordUiObservationAsync(
        string sessionId, ReportUiObservationApiRequest request,
        IEllaiUiObservationStore observations, CancellationToken cancellationToken)
    {
        var accepted = await observations.RecordAsync(new StoredUiObservationReport(
            sessionId, request.ViewInstanceId.Trim(), request.ContextStamp.Trim(),
            request.Platform.Trim(), request.MountedControlIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToList(),
            DateTimeOffset.UtcNow, request.ControlStates, request.CurrentNavigationId, request.CompletedControlIds, request.ValidationInputIds, request.InputStates), cancellationToken);
        return accepted
            ? Results.Json(true, statusCode: StatusCodes.Status200OK)
            : Results.Conflict();
    }

    private static async Task<IResult> GrantConsentAsync(
        string sessionId,
        GrantEllaiConsentApiRequest? request,
        EllaiConsentService consent,
        IEllaiConsentStore consents,
        CancellationToken cancellationToken)
    {
        // A client grant records user preference only; it cannot assert a
        // safe processing classification for arbitrary user supplied text.
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || !Enum.TryParse<EllaiConsentDimension>(request.Dimension, ignoreCase: true, out var dimension)
            || !Enum.IsDefined(dimension)
            || string.IsNullOrWhiteSpace(request.Purpose)
            || request.Purpose.Length > 120
            || request.HoursToLive is < 1 or > 24
            || (!string.Equals(request.Purpose, EllaiToolBroker.ToolReadPurpose, StringComparison.Ordinal)
                && !string.Equals(request.Purpose, EllaiConsentPurposes.PublicProductHelp, StringComparison.Ordinal)
                && !string.Equals(request.Purpose, EllaiConsentPurposes.VoiceTranscription, StringComparison.Ordinal))
            || (string.Equals(request.Purpose, EllaiConsentPurposes.PublicProductHelp, StringComparison.Ordinal)
                && (dimension != EllaiConsentDimension.ReasoningText || request.HoursToLive != 1))
            || (string.Equals(request.Purpose, EllaiConsentPurposes.VoiceTranscription, StringComparison.Ordinal)
                && (dimension is not (EllaiConsentDimension.Microphone or EllaiConsentDimension.Transcription)
                    || request.HoursToLive != 1)))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai consent grant is invalid.");
        }

        var dataClass = string.Equals(request.Purpose, EllaiConsentPurposes.PublicProductHelp, StringComparison.Ordinal)
            ? EllaiProcessingClassification.ConsentedNonSensitive
            : string.Equals(request.Purpose, EllaiConsentPurposes.VoiceTranscription, StringComparison.Ordinal)
                ? EllaiVoiceTranscriptionService.AudioDataClass
                : EllaiProcessingClassification.UnknownOrigin;
        var grant = consent.Grant(sessionId, dimension, request.Purpose,
            dataClass, TimeSpan.FromHours(request.HoursToLive));
        try
        {
            await consents.RecordAsync(grant, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never leave a memory-only grant behind a durable failure:
            // revoke locally so both lanes agree the grant does not exist.
            consent.Revoke(grant.GrantId);
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The Ellai consent store is unavailable.");
        }

        return Results.Json(grant, statusCode: StatusCodes.Status200OK);
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

    private static async Task<IResult> RevokeConsentAsync(
        string sessionId,
        string grantId,
        EllaiConsentService consent,
        IEllaiConsentStore consents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grantId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai consent revocation is invalid.");
        }

        var owned = await consents.ListBySessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (owned.All(grant => grant.GrantId != grantId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "The Ellai consent grant was not found for this session.");
        }

        consent.Revoke(grantId);
        var revoked = await consents.SetRevokedAsync(sessionId, grantId, cancellationToken).ConfigureAwait(false);
        if (!revoked)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The Ellai consent store is unavailable.");
        }

        return Results.Json(true, statusCode: StatusCodes.Status200OK);
    }

    private static Task<IResult> RejectReviewAsync(
        string sessionId,
        string requestId,
        RejectReviewApiRequest? request,
        EllaiApprovalDialogCoordinator dialogs,
        EllaiSessionEventJournal journals,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal))
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai review rejection is invalid."));
        }

        var now = DateTimeOffset.UtcNow;
        var review = dialogs.Get(requestId);
        if (review is null
            || !string.Equals(review.SessionId, sessionId, StringComparison.Ordinal)
            || review.State != EllaiApprovalDialogState.AwaitingApproval)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The approval review is stale, superseded or already closed."));
        }

        dialogs.Resolve(requestId, approved: false, now);
        journals.Append(sessionId, "approval-resolved", requestId, "Rejected", now);
        return Task.FromResult(Results.Json(true, statusCode: StatusCodes.Status200OK));
    }
}

/// <summary>Cancel request carrying the officer-observed expected version.</summary>
public sealed record CancelEllaiSessionApiRequest(long ExpectedSessionVersion);

/// <summary>Authenticated consent grant for one session: dimension plus a
/// purpose from the closed purpose set, with a bounded lifetime. Recording
/// the grant lets consented utterances reach the reasoning lane; it never
/// approves an administrative action and never grants another session.</summary>
public sealed record GrantEllaiConsentApiRequest(
    string SessionId,
    string Dimension,
    string Purpose,
    int HoursToLive);

/// <summary>Client-posted mounted-control report for the live UI
/// observation channel. At most 20 control ids; the server stamps receipt
/// time and keeps only the latest report per session.</summary>
public sealed record ReportUiObservationApiRequest(
    string SessionId,
    string ViewInstanceId,
    string ContextStamp,
    string Platform,
    IReadOnlyList<string> MountedControlIds,
    IReadOnlyList<UiControlState>? ControlStates = null,
    string? CurrentNavigationId = null,
    IReadOnlyList<string>? CompletedControlIds = null,
    IReadOnlyList<string>? ValidationInputIds = null,
    IReadOnlyList<UiInputState>? InputStates = null);

/// <summary>Review registration for a validated pending plan: binds the
/// dialog instance to one server-tracked review (duplicate ids focus it).
/// Display rows derive from the exact proposals; approval binds them.</summary>
public sealed record RegisterApprovalReviewApiRequest(
    string SessionId,
    string RequestId,
    string PlanHash,
    int PlanVersion,
    IReadOnlyList<AdminActionProposal>? Proposals = null);

/// <summary>Human edit of a plan under review: supersedes the old binding
/// and opens the revised exact version for fresh review.</summary>
public sealed record EditApprovalReviewApiRequest(
    string SessionId,
    string NewRequestId,
    string NewPlanHash,
    int NewPlanVersion,
    IReadOnlyList<AdminActionProposal>? Proposals = null);

/// <summary>Human selection from the open dialog: the stable action ids the
/// user explicitly approved. The server resolves them against the reviewed
/// exact proposals; unknown ids reject the batch.</summary>
public sealed record ApproveSelectedApiRequest(
    string SessionId,
    IReadOnlyList<string> ActionIds);

/// <summary>Human rejection of the open dialog review.</summary>
public sealed record RejectReviewApiRequest(string SessionId);

/// <summary>Exact-approval submission: the approved fields must match the
/// proposed plan exactly or the service returns null (mapped to 422).
/// When the authenticated dialog presents a review, <c>ApprovalRequestId</c>
/// binds the submission to that open review (mapped to 409 when stale);
/// it stays optional until the dialog host ships.</summary>
public sealed record SubmitApprovalApiRequest(
    string SessionId,
    string PlanHash,
    int PlanVersion,
    AdminActionProposal Proposal,
    string ApprovedPlanHash,
    int ApprovedPlanVersion,
    string? ApprovedRecipient,
    DateTimeOffset? ApprovedDueAtUtc,
    string? ApprovalRequestId = null);

internal static class EllaiEndpointGuards
{
    internal static bool FinalizedBoundsCheckFails(this StartSessionRequest request) =>
        request.Mode is < EllaiMode.Ask or > EllaiMode.ReviewEvidence;
}
