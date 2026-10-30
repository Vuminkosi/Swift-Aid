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

    private static async Task<IResult> CancelAsync(
        string sessionId,
        CancelEllaiSessionApiRequest? request,
        EllaiSessionService sessions,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The Ellai cancel request is invalid.");
        }

        var cancelled = await sessions.CancelAsync(sessionId, request.ExpectedSessionVersion, cancellationToken);
        if (!cancelled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The Ellai session changed before cancellation was recorded.");
        }

        return Results.Ok();
    }
}

/// <summary>Cancel request carrying the officer-observed expected version.</summary>
public sealed record CancelEllaiSessionApiRequest(long ExpectedSessionVersion);

internal static class EllaiEndpointGuards
{
    internal static bool FinalizedBoundsCheckFails(this StartSessionRequest request) =>
        request.Mode is < EllaiMode.Ask or > EllaiMode.ReviewEvidence;
}
