using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Endpoints;

public static class CaseMonitoringPageEndpoints
{
    public static void MapCaseMonitoringPageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/cases/{caseId:guid}/monitoring", GetAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" });
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid caseId, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }

        var offsetText = context.Request.Query["offset"].ToString();
        var limitText = context.Request.Query["limit"].ToString();
        var versionText = context.Request.Query["version"].ToString();
        if (caseId == Guid.Empty || !int.TryParse(offsetText, out var offset) || offset is < 0 or > 1_000_000 ||
            !int.TryParse(limitText, out var limit) || limit is < 1 or > 100 ||
            !long.TryParse(versionText, out var version) || version < 1)
            return Results.Problem(statusCode: 400, title: "The case work page request is invalid.");

        var query = context.RequestServices.GetService<ICaseMonitoringPageQuery>();
        if (query is null) return Results.Problem(statusCode: 503, title: "Case work is temporarily unavailable.");
        try
        {
            var result = await query.GetAsync(actor, caseId, offset, limit, version, cancellationToken).ConfigureAwait(false);
            return result.Access switch
            {
                CaseMonitoringPageAccess.Found when result.Page is not null => Results.Json(result.Page),
                CaseMonitoringPageAccess.NotFoundOrNotAuthorized => Results.Problem(statusCode: 404, title: "The case was not found."),
                CaseMonitoringPageAccess.Stale => Results.Problem(statusCode: 409, title: "The case changed. Refresh and try again."),
                _ => Results.Problem(statusCode: 503, title: "Case work is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 503, title: "Case work is temporarily unavailable."); }
    }
}
