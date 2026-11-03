using System.Globalization;
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
/// identifier, so the response never reveals draft, reviewed, or internal work.
/// Previously published completed sessions remain public. No attendee, case, or audit data leaves through these routes. A deployment
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
        routes.AddEndpointFilter(async (context, next) =>
        {
            // Session status and public destinations can change on cancellation or revision.
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/", ListAsync)
            .AllowAnonymous()
            .WithName("ListPublicSessions")
            .Produces<IReadOnlyList<PublicSessionView>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        routes.MapGet("/search", SearchAsync)
            .AllowAnonymous()
            .WithName("SearchPublicSessions")
            .Produces<PublicSessionPage>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
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
        routes.MapGet("/{sessionId:guid}/destination", GetDestinationAsync)
            .AllowAnonymous()
            .WithName("OpenPublicSessionDestination")
            .Produces(StatusCodes.Status302Found)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Maps a missing publication to the generic public 404.</summary>
    public static IResult MapMissing() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The community session was not found.");

    private static async Task<IResult> SearchAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var values = context.Request.Query;
        if (!TryDate(values["from"], out var from) || !TryDate(values["before"], out var before) ||
            !TryDate(values["afterStart"], out var afterStart) ||
            !TryLocalDate(values["localFrom"], out var localFrom) || !TryLocalDate(values["localTo"], out var localTo) ||
            (!string.IsNullOrWhiteSpace(values["afterId"]) && !Guid.TryParse(values["afterId"], out _)) ||
            (!string.IsNullOrWhiteSpace(values["limit"]) && !int.TryParse(values["limit"], out _)))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The session search is invalid.");
        }

        var query = new PublicSessionQuery(
            Search: values["search"], CountryCode: values["country"], RegionId: values["region"],
            Sector: values["sector"], Language: values["language"], Topic: values["topic"],
            StartFromUtc: from, StartBeforeUtc: before, AfterStartUtc: afterStart,
            AfterSessionId: Guid.TryParse(values["afterId"], out var afterId) ? afterId : null,
            Limit: int.TryParse(values["limit"], out var limit) ? limit : 25,
            Category: values["category"], LocalDateFrom: localFrom, LocalDateTo: localTo, Status: values["status"]);
        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Community sessions are temporarily unavailable.");
        }

        try
        {
            var page = await service.QueryPublicAsync(query, cancellationToken).ConfigureAwait(false);
            context.Response.Headers[PublicSessionQueryFeatures.HeaderName] = PublicSessionQueryFeatures.StatusFilter;
            return Results.Json(page, options: JsonOptions);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The session search is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Community sessions are temporarily unavailable.");
        }
    }

    private static bool TryDate(string? value, out DateTimeOffset? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        result = parsed;
        return true;
    }

    private static bool TryLocalDate(string? value, out DateOnly? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        result = parsed;
        return true;
    }

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

    private static async Task<IResult> GetDestinationAsync(HttpContext context, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(context.Request.Query["version"], NumberStyles.None, CultureInfo.InvariantCulture, out var expectedVersion) || expectedVersion < 1)
            return DestinationUnavailable();

        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null) return DestinationUnavailable();
        try
        {
            var session = await service.GetPublicAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var link = session?.ChannelLink;
            if (session is null || session.SessionId != sessionId || session.Version != expectedVersion ||
                !string.Equals(session.Status, "Scheduled", StringComparison.Ordinal) ||
                session.ChannelType == SessionChannelType.None ||
                string.IsNullOrWhiteSpace(link) || link.Any(char.IsControl) ||
                !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo))
                return DestinationUnavailable();

            return Results.Redirect(uri.AbsoluteUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Community sessions are temporarily unavailable.");
        }
    }

    private static IResult DestinationUnavailable() => Results.Problem(statusCode: StatusCodes.Status404NotFound,
        title: "The session destination is unavailable or changed. Refresh the session and try again.");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
