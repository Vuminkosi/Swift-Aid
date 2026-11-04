using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Endpoints;

public static class CaseWorkDiscoveryEndpoints
{
    public static void MapCaseWorkDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/cases/monitoring/cases", GetAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" });
    }

    private static async Task<IResult> GetAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var values = context.Request.Query;
        if (!Enum.TryParse<CaseWorkDiscoveryKind>(values["kind"].ToString(), false, out var kind) || !Enum.IsDefined(kind) ||
            !Enum.TryParse<CaseWorkDiscoveryScope>(values["scope"].ToString(), false, out var scope) || !Enum.IsDefined(scope) ||
            !int.TryParse(values["limit"].ToString(), out var limit) || limit is < 1 or > 100 ||
            kind == CaseWorkDiscoveryKind.Escalation && scope == CaseWorkDiscoveryScope.Assigned)
            return Results.Problem(statusCode: 400, title: "The work request is invalid.");
        Guid? cursor = null;
        if (values.TryGetValue("cursor", out var rawCursor))
        {
            if (rawCursor.Count != 1 || !Guid.TryParseExact(rawCursor[0], "N", out var parsed) || parsed == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "The work cursor is invalid.");
            cursor = parsed;
        }
        var query = context.RequestServices.GetService<ICaseWorkDiscoveryQuery>();
        if (query is null) return Results.Problem(statusCode: 503, title: "Work is temporarily unavailable.");
        try
        {
            var result = await query.GetAsync(actor, kind, scope, limit, cursor, cancellationToken).ConfigureAwait(false);
            return result.Access == CaseQueueAccess.Allowed
                ? Results.Json(result)
                : Results.Problem(statusCode: 503, title: "Work is temporarily unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Problem(statusCode: 503, title: "Work is temporarily unavailable."); }
    }
}
