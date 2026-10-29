using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>
/// Maps the authenticated staff endpoint for scoped surveillance area aggregates.
/// The aggregate covers only managed cases owned by the caller that the caller
/// may still read; denied cases are skipped, never listed. An empty aggregate
/// is a normal 200 with an empty area list: it never reveals whether other
/// cases exist or who may read them.
/// </summary>
public static class MapAggregateEndpoints
{
    public static void MapMapAggregateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schemes = string.Join(
            ",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        endpoints.MapGet("/api/v1/surveillance/area-aggregates", GetAggregatesAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithName("GetSurveillanceAreaAggregates")
            .Produces<SurveillanceAggregateResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetAggregatesAsync(
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

        // The aggregate query only exists when the case command pipeline is enabled.
        // A deployment without it fails closed with the same generic 404.
        var query = context.RequestServices.GetService<IMapAggregateQuery>();
        if (query is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Surveillance aggregates were not found.");
        }

        string? regionCode = null;
        if (context.Request.Query.TryGetValue("regionCode", out var regionText) && regionText.Count > 0 &&
            !string.IsNullOrWhiteSpace(regionText[0]))
        {
            regionCode = regionText[0];
        }

        if (!SurveillanceMapAggregateQuery.IsRegionCode(regionCode))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The region code is invalid.");
        }

        var window = TimeSpan.FromDays(30);
        if (context.Request.Query.TryGetValue("period", out var periodText) && periodText.Count > 0 &&
            !string.IsNullOrWhiteSpace(periodText[0]))
        {
            window = periodText[0] switch
            {
                { } period when string.Equals(period, "days30", StringComparison.OrdinalIgnoreCase) =>
                    TimeSpan.FromDays(30),
                { } period when string.Equals(period, "days90", StringComparison.OrdinalIgnoreCase) =>
                    TimeSpan.FromDays(90),
                _ => TimeSpan.Zero
            };
        }

        if (window == TimeSpan.Zero)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The aggregate period is invalid.");
        }

        try
        {
            var result = await query.GetAsync(actor, regionCode, window, cancellationToken).ConfigureAwait(false);
            return result.Access switch
            {
                SurveillanceAggregateAccess.Allowed => Results.Json(
                    result, options: JsonOptions, statusCode: StatusCodes.Status200OK),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Surveillance aggregates are temporarily unavailable.")
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
                title: "Surveillance aggregates are temporarily unavailable.");
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
