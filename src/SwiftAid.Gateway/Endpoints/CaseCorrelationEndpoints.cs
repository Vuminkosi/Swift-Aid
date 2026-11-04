using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Correlation;

namespace SwiftAid.Gateway.Endpoints;

public static class CaseCorrelationEndpoints
{
    public static void MapCaseCorrelationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/cases/{caseId:guid}/correlation-candidates", GetAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" });
        endpoints.MapGet("/api/v1/cases/{caseId:guid}/correlation-candidates/page", GetPageAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" });
    }

    private static async Task<IResult> GetPageAsync(HttpContext context, Guid caseId, CancellationToken ct)
    {
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var query = context.RequestServices.GetService<CaseCorrelationQuery>();
        if (query is null || caseId == Guid.Empty) return Results.Problem(statusCode: 404, title: "The case was not found.");
        var cursor = context.Request.Query.TryGetValue("cursor", out var values) ? values.ToString() : null;
        try
        {
            var page = await query.GetPageAsync(actor, caseId, cursor, ct).ConfigureAwait(false);
            return page.Allowed ? Results.Json(page) : Results.Problem(statusCode: 404, title: "The case was not found.");
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "The correlation cursor is invalid."); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "Correlation candidates are temporarily unavailable."); }
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid caseId, CancellationToken ct)
    {
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var query = context.RequestServices.GetService<CaseCorrelationQuery>();
        if (query is null || caseId == Guid.Empty) return Results.Problem(statusCode: 404, title: "The case was not found.");
        try
        {
            var result = await query.GetAsync(actor, caseId, ct).ConfigureAwait(false);
            return result.Allowed ? Results.Json(result.Candidates) : Results.Problem(statusCode: 404, title: "The case was not found.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "Correlation candidates are temporarily unavailable."); }
    }
}
