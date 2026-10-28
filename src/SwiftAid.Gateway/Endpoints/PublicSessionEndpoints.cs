using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Maps the anonymous public endpoints for published community sessions.</summary>
/// <remarks>
/// Only the service-published projection is reachable here: a session that is not
/// published with public visibility resolves to the same generic 404 as an unknown
/// identifier, so the response never reveals draft, reviewed, internal, or completed
/// work. No attendee, case, or audit data leaves through these routes. A deployment
/// without the session pipeline fails closed with the same generic 404.
/// </remarks>
public static class PublicSessionEndpoints
{
    /// <summary>Maximum public page size; larger requests are rejected, not silently served.</summary>
    public const int MaxLimit = 100;

    public static void MapPublicSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var routes = endpoints.MapGroup("/public/v1/sessions");
        routes.MapGet("/", ListAsync)
            .AllowAnonymous()
            .WithName("ListPublicSessions")
            .Produces<IReadOnlyList<PublicSessionView>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        routes.MapGet("/{sessionId:guid}", GetAsync)
            .AllowAnonymous()
            .WithName("GetPublicSession")
            .Produces<PublicSessionView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        routes.MapGet("/{sessionId:guid}/calendar", GetCalendarAsync)
            .AllowAnonymous()
            .WithName("GetPublicSessionCalendar")
            .Produces(StatusCodes.Status200OK, contentType: "text/calendar")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        routes.MapGet("/{sessionId:guid}/invitation", GetInvitationAsync)
            .AllowAnonymous()
            .WithName("GetPublicSessionInvitation")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }

    /// <summary>Maps a missing publication to the generic public 404.</summary>
    public static IResult MapMissing() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The community session was not found.");

    private static async Task<IResult> ListAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return MapMissing();
        }

        int? limit = null;
        if (context.Request.Query.TryGetValue("limit", out var limitText) && limitText.Count > 0 &&
            !string.IsNullOrWhiteSpace(limitText[0]))
        {
            if (!int.TryParse(limitText[0], out var parsed) || parsed < 1 || parsed > MaxLimit)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The page limit is invalid.");
            }

            limit = parsed;
        }

        try
        {
            var sessions = await service.ListPublicAsync(limit, cancellationToken).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Json(sessions, options: JsonOptions, statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Community sessions are temporarily unavailable.");
        }
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return MapMissing();
        }

        try
        {
            var session = await service.GetPublicAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                return MapMissing();
            }

            context.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Json(session, options: JsonOptions, statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Community sessions are temporarily unavailable.");
        }
    }

    private static async Task<IResult> GetCalendarAsync(
        HttpContext context,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return MapMissing();
        }

        try
        {
            var ics = await service.GetIcsAsync(sessionId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            if (ics is null)
            {
                return MapMissing();
            }

            var bytes = Encoding.UTF8.GetBytes(ics);
            return Results.File(bytes, "text/calendar", $"session-{sessionId:D}.ics");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Community sessions are temporarily unavailable.");
        }
    }

    private static async Task<IResult> GetInvitationAsync(
        HttpContext context,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return MapMissing();
        }

        try
        {
            var invitation = await service.GetInvitationTextAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (invitation is null)
            {
                return MapMissing();
            }

            return Results.Content(invitation, "text/plain; charset=utf-8");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Community sessions are temporarily unavailable.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
