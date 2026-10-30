using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Endpoints;

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

        var schemes = string.Join(
            ",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        var group = endpoints.MapGroup("/api/v1/ellai");
        group.MapPost("/sessions", StartAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("StartEllaiSession")
            .Accepts<StartSessionRequest>("application/json")
            .Produces<EllaiSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
        group.MapPost("/sessions/{sessionId}/utterances", SubmitUtteranceAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
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
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("SubmitEllaiApproval")
            .Accepts<SubmitApprovalApiRequest>("application/json")
            .Produces<EllaiReceipt>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/sessions/{sessionId}/events", PollEventsAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithName("PollEllaiSessionEvents")
            .Produces<EllaiSessionEventPage>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{sessionId}/receipts", ListReceipts)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithName("ListEllaiReceipts")
            .Produces<IReadOnlyList<EllaiReceipt>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapPost("/sessions/{sessionId}/approval-reviews", RegisterReviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("RegisterEllaiApprovalReview")
            .Accepts<RegisterApprovalReviewApiRequest>("application/json")
            .Produces<EllaiApprovalDialogRequest>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
        group.MapGet("/sessions/{sessionId}/approval-reviews/active", GetActiveReviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithName("GetEllaiActiveApprovalReview")
            .Produces<EllaiApprovalDialogRequest>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        group.MapPost("/sessions/{sessionId}/approval-reviews/{requestId}/edit", EditReviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("EditEllaiApprovalReview")
            .Accepts<EditApprovalReviewApiRequest>("application/json")
            .Produces<EllaiApprovalDialogRequest>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        group.MapPost("/sessions/{sessionId}/approval-reviews/{requestId}/approve", ApproveSelectedAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("ApproveEllaiSelectedActions")
            .Accepts<ApproveSelectedApiRequest>("application/json")
            .Produces<IReadOnlyList<EllaiReceipt>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/sessions/{sessionId}/approval-reviews/{requestId}/reject", RejectReviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("RejectEllaiApprovalReview")
            .Accepts<RejectReviewApiRequest>("application/json")
            .Produces<bool>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        group.MapPost("/sessions/{sessionId}/ui-observations", ReportUiObservationAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("ReportEllaiUiObservation")
            .Accepts<ReportUiObservationApiRequest>("application/json")
            .Produces<bool>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
        group.MapPost("/sessions/{sessionId}/consents", GrantConsentAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("GrantEllaiConsent")
            .Accepts<GrantEllaiConsentApiRequest>("application/json")
            .Produces<EllaiConsentGrant>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
        group.MapPost("/sessions/{sessionId}/cancel", CancelAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
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

        var view = await sessions.StartAsync(request, cancellationToken);
        return Results.Json(view, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> SubmitUtteranceAsync(
        string sessionId,
        SubmitUtteranceRequest? request,
        EllaiSessionService sessions,
        HttpContext http,
        IEllaiSessionStore sessionStore,
        EllaiConsentService consent,
        EllaiUtteranceQueue utterances,
        EllaiSessionEventJournal journals,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.FinalizedText)
            || request.FinalizedText.Length > 8000
            || (request.FeatureId is not null
                && (string.IsNullOrWhiteSpace(request.FeatureId) || request.FeatureId.Length > 120))
            || (request.ResourceId is not null
                && (string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 120)))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai utterance is invalid.");
        }

        var view = await sessions.SubmitUtteranceAsync(request, cancellationToken);
        if (view is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The Ellai session changed before the utterance was recorded.");
        }

        // Best-effort background reasoning: the recorded view returns
        // immediately; progress and results flow through the session event
        // journal. A refused trigger journals why instead of going silent.
        ActorContext actor;
        try
        {
            actor = ActorContext.FromValidatedPrincipal(http.User);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The Ellai utterance needs an authenticated actor.");
        }

        var record = await sessionStore.GetAsync(sessionId, cancellationToken);
        if (record is null)
        {
            journals.Append(sessionId, "activity", "blocked",
                "That information isn't available right now.", DateTimeOffset.UtcNow);
            return Results.Json(view, statusCode: StatusCodes.Status200OK);
        }

        var feature = string.IsNullOrWhiteSpace(request.FeatureId) ? "general" : request.FeatureId.Trim();
        var work = new EllaiUtteranceWork(
            sessionId, record.ScopeEpoch, request.FinalizedText.Trim(), feature,
            string.IsNullOrWhiteSpace(request.ResourceId) ? null : request.ResourceId.Trim(),
            record.Mode, record.InteractionEpoch, $"cycle-{record.CyclesUsed}",
            actor,
            EllaiPolicyService.IsContributorEligible(
                EllaiUtteranceRunner.ConsentedLabel, "general"),
            consent.VersionLabel(sessionId));
        if (!utterances.TryEnqueue(work))
        {
            journals.Append(sessionId, "activity", "blocked",
                "That information isn't available right now.", DateTimeOffset.UtcNow);
        }

        return Results.Json(view, statusCode: StatusCodes.Status200OK);
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
        return Results.Json(list, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> CancelAsync(
        string sessionId,
        CancelEllaiSessionApiRequest? request,
        EllaiSessionService sessions,
        EllaiApprovalDialogCoordinator dialogs,
        EllaiSessionEventJournal journals,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai cancel request is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        dialogs.CancelSession(sessionId, now);
        journals.Append(sessionId, "session-cancelled", null, "Stopped.", now);
        var cancelled = await sessions.CancelAsync(sessionId, request.ExpectedSessionVersion, cancellationToken);
        if (!cancelled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The Ellai session changed before cancellation was recorded.");
        }

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
        return Results.Json(receipts, statusCode: StatusCodes.Status200OK);
    }

    private static Task<IResult> ReportUiObservationAsync(
        string sessionId,
        ReportUiObservationApiRequest? request,
        IEllaiUiObservationStore observations,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.ViewInstanceId)
            || string.IsNullOrWhiteSpace(request.ContextStamp)
            || string.IsNullOrWhiteSpace(request.Platform)
            || request.MountedControlIds is null
            || request.MountedControlIds.Count == 0
            || request.MountedControlIds.Count > InMemoryEllaiUiObservationStore.MaxMountedControls
            || request.MountedControlIds.Any(id =>
                string.IsNullOrWhiteSpace(id) || id.Length > 120))
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
        await observations.RecordAsync(new StoredUiObservationReport(
            sessionId, request.ViewInstanceId.Trim(), request.ContextStamp.Trim(),
            request.Platform.Trim(), request.MountedControlIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToList(),
            DateTimeOffset.UtcNow), cancellationToken);
        return Results.Json(true, statusCode: StatusCodes.Status200OK);
    }

    private static Task<IResult> GrantConsentAsync(
        string sessionId,
        GrantEllaiConsentApiRequest? request,
        EllaiConsentService consent,
        CancellationToken cancellationToken)
    {
        // Sessions are not owner-bound yet (pre-existing gap, reported):
        // this records a grant only for the named session and returns only
        // that grant's safe fields, never another session's state.
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || !Enum.TryParse<EllaiConsentDimension>(request.Dimension, ignoreCase: true, out var dimension)
            || !Enum.IsDefined(dimension)
            || string.IsNullOrWhiteSpace(request.Purpose)
            || request.Purpose.Length > 120
            || !string.Equals(request.Purpose, EllaiToolBroker.ToolReadPurpose, StringComparison.Ordinal)
            || request.HoursToLive is < 1 or > 24)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai consent grant is invalid."));
        }

        var grant = consent.Grant(sessionId, dimension, request.Purpose,
            EllaiUtteranceRunner.ConsentedLabel, TimeSpan.FromHours(request.HoursToLive));
        return Task.FromResult(Results.Json(grant, statusCode: StatusCodes.Status200OK));
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
    IReadOnlyList<string> MountedControlIds);

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
