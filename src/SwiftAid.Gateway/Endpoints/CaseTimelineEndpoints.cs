using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Maps the authenticated staff endpoint for the managed-case immutable timeline.</summary>
/// <remarks>
/// Denial and absence share one generic 404 so the response never reveals whether a case
/// exists or who may read it. The timeline carries event metadata only; sealed report
/// content and payload bytes are never returned here.
/// </remarks>
public static class CaseTimelineEndpoints
{
    public static void MapCaseTimelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schemes = string.Join(
            ",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        endpoints.MapGet("/api/v1/cases/{caseId:guid}/timeline", GetTimelineAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithName("GetCaseTimeline")
            .Produces<CaseTimeline>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }

    /// <summary>Maps query outcomes to HTTP; denial and absence share one generic 404.</summary>
    public static IResult MapResult(CaseTimelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Access == CaseTimelineAccess.Found && result.Timeline is not null
            ? Results.Json(result.Timeline, options: JsonOptions, statusCode: StatusCodes.Status200OK)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The case was not found.");
    }

    private static async Task<IResult> GetTimelineAsync(
        HttpContext context,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        ActorContext? actor;
        try
        {
            actor = TryGetActor(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (actor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        // The timeline query only exists when the case command pipeline is enabled.
        // A deployment without it fails closed with the same generic 404.
        var query = context.RequestServices.GetService<ICaseTimelineQuery>();
        if (query is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The case was not found.");
        }

        if (caseId == Guid.Empty)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The case identifier is invalid.");
        }

        int? limit = null;
        if (context.Request.Query.TryGetValue("limit", out var limitText) && limitText.Count > 0 &&
            !string.IsNullOrWhiteSpace(limitText[0]))
        {
            if (!int.TryParse(limitText[0], out var parsed) || parsed < 1)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The page limit is invalid.");
            }

            limit = Math.Min(parsed, CaseTimelineQuery.MaxLimit);
        }

        try
        {
            var result = await query.GetAsync(actor, caseId, limit, cancellationToken).ConfigureAwait(false);
            return MapResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The case timeline is temporarily unavailable.");
        }
    }

    private static ActorContext? TryGetActor(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return ActorContext.FromValidatedPrincipal(context.User);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
