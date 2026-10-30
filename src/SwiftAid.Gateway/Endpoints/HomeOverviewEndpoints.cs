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

/// <summary>
/// Maps the authenticated staff endpoint for the privacy-minimized home
/// overview. Counts cover only cases the caller may read; assistance requests
/// are reported as not collected rather than zero. An empty overview is a
/// normal 200: it never reveals whether other cases exist or who may read them.
/// </summary>
public static class HomeOverviewEndpoints
{
    public static void MapHomeOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schemes = string.Join(
            ",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        endpoints.MapGet("/api/v1/home/overview", GetOverviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithName("GetHomeOverview")
            .Produces<HomeOverviewResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetOverviewAsync(
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

        // The home query only exists when the case command pipeline is enabled.
        // A deployment without it fails closed with the same generic 404.
        var query = context.RequestServices.GetService<IHomeOverviewQuery>();
        if (query is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The home overview was not found.");
        }

        var recentActivityLimit = 5;
        if (context.Request.Query.TryGetValue("recentActivityLimit", out var limitText) &&
            limitText.Count > 0 && !string.IsNullOrWhiteSpace(limitText[0]))
        {
            if (!int.TryParse(limitText[0], out var parsed) || parsed < 0 || parsed > 5)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The activity limit is invalid.");
            }

            recentActivityLimit = parsed;
        }

        try
        {
            var result = await query.GetAsync(actor, recentActivityLimit, cancellationToken).ConfigureAwait(false);
            return result.Access switch
            {
                HomeOverviewAccess.Allowed => Results.Json(
                    result, options: JsonOptions, statusCode: StatusCodes.Status200OK),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The home overview is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The home overview is temporarily unavailable.");
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
