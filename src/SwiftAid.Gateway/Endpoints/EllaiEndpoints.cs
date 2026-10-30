using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
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
        CancellationToken cancellationToken)
    {
        if (request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.FinalizedText)
            || request.FinalizedText.Length > 8000)
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

        return Results.Json(view, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> SubmitApprovalAsync(
        string sessionId,
        SubmitApprovalApiRequest? request,
        EllaiApprovalService approvals,
        EllaiApprovalDialogCoordinator dialogs,
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
            dialogs.Resolve(request.ApprovalRequestId, receipt is not null, now);
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
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai cancel request is invalid.");
        }

        dialogs.CancelSession(sessionId, DateTimeOffset.UtcNow);
        var cancelled = await sessions.CancelAsync(sessionId, request.ExpectedSessionVersion, cancellationToken);
        if (!cancelled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The Ellai session changed before cancellation was recorded.");
        }

        return Results.Ok();
    }

    private static Task<IResult> RegisterReviewAsync(
        string sessionId,
        RegisterApprovalReviewApiRequest? request,
        EllaiApprovalDialogCoordinator dialogs,
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

        var (review, _) = dialogs.Register(sessionId, request.RequestId, request.PlanHash,
            request.PlanVersion, DateTimeOffset.UtcNow, EllaiApprovalService.ApprovalLifetime);
        return Task.FromResult(Results.Json(review, statusCode: StatusCodes.Status200OK));
    }

    private static Task<IResult> EditReviewAsync(
        string sessionId,
        string requestId,
        EditApprovalReviewApiRequest? request,
        EllaiApprovalDialogCoordinator dialogs,
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

        var next = dialogs.ApplyEdit(requestId, request.NewRequestId, request.NewPlanHash,
            request.NewPlanVersion, DateTimeOffset.UtcNow, EllaiApprovalService.ApprovalLifetime);
        if (next is null)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The approval review is stale, superseded or already closed."));
        }

        return Task.FromResult(Results.Json(next, statusCode: StatusCodes.Status200OK));
    }
}

/// <summary>Cancel request carrying the officer-observed expected version.</summary>
public sealed record CancelEllaiSessionApiRequest(long ExpectedSessionVersion);

/// <summary>Review registration for a validated pending plan: binds the
/// dialog instance to one server-tracked review (duplicate ids focus it).</summary>
public sealed record RegisterApprovalReviewApiRequest(
    string SessionId,
    string RequestId,
    string PlanHash,
    int PlanVersion);

/// <summary>Human edit of a plan under review: supersedes the old binding
/// and opens the revised exact version for fresh review.</summary>
public sealed record EditApprovalReviewApiRequest(
    string SessionId,
    string NewRequestId,
    string NewPlanHash,
    int NewPlanVersion);

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
