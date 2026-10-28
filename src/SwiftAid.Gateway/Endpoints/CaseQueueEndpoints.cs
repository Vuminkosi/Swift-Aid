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

/// <summary>Maps the authenticated staff endpoint for the officer "my cases" queue.</summary>
/// <remarks>
/// An empty queue is a normal 200 with an empty list: it never reveals whether
/// other cases exist or who may read them. Per-case denial is enforced by the
/// query, which skips cases the officer may not read instead of listing them.
/// </remarks>
public static class CaseQueueEndpoints
{
    public static void MapCaseQueueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schemes = string.Join(
            ",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        endpoints.MapGet("/api/v1/cases/mine", GetMineAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithName("GetMyCases")
            .Produces<CaseQueueResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }

    /// <summary>Maps queue outcomes to HTTP; denial stays a 200 empty list with its flag.</summary>
    public static IResult MapResult(CaseQueueResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Access switch
        {
            CaseQueueAccess.Allowed or CaseQueueAccess.Denied => Results.Json(
                result, options: JsonOptions, statusCode: StatusCodes.Status200OK),
            _ => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The case queue is temporarily unavailable.")
        };
    }

    private static async Task<IResult> GetMineAsync(
        HttpContext context,
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

        // The queue query only exists when the case command pipeline is enabled.
        // A deployment without it fails closed with the same generic 404.
        var query = context.RequestServices.GetService<ICaseQueueQuery>();
        if (query is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The case queue was not found.");
        }

        int? limit = null;
        if (context.Request.Query.TryGetValue("limit", out var limitText) && limitText.Count > 0 &&
            !string.IsNullOrWhiteSpace(limitText[0]))
        {
            if (!int.TryParse(limitText[0], out var parsed) || parsed < 1)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The page limit is invalid.");
            }

            limit = Math.Min(parsed, CaseQueueQuery.MaxLimit);
        }

        try
        {
            var result = await query.GetMineAsync(actor, limit, cancellationToken).ConfigureAwait(false);
            return MapResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The case queue is temporarily unavailable.");
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
