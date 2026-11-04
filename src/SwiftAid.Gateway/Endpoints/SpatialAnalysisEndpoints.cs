using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Spatial;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Gateway.Endpoints;

public static class SpatialAnalysisEndpoints
{
    public static void MapSpatialAnalysisEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/cases/spatial/owned-regions", GetAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" });
    }

    private static async Task<IResult> GetAsync(HttpContext context, CancellationToken ct)
    {
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var analysis = context.RequestServices.GetService<OwnedCaseSpatialAnalysis>();
        if (analysis is null) return Results.Problem(statusCode: 503, title: "Spatial analysis is unavailable.");
        try
        {
            var result = await analysis.GetAsync(actor, ct).ConfigureAwait(false);
            return result.Access == SpatialAnalysisAccess.Allowed ? Results.Json(result) :
                Results.Problem(statusCode: 503, title: "A complete spatial analysis is unavailable.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "Spatial analysis is unavailable."); }
    }
}
