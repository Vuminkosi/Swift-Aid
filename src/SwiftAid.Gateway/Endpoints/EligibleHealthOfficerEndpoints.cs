using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Case-scoped candidates for human escalation review; this endpoint does not assign officers.</summary>
public static class EligibleHealthOfficerEndpoints
{
    public static void MapEligibleHealthOfficerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var schemes = string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        endpoints.MapGet("/api/v1/cases/{caseId:guid}/eligible-health-officers", GetAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes });
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid caseId, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }

        var query = context.RequestServices.GetService<IEligibleHealthOfficerQuery>();
        if (query is null) return NotFound();
        if (caseId == Guid.Empty) return Results.Problem(statusCode: 400, title: "The case identifier is invalid.");
        var limit = 20;
        if (context.Request.Query.TryGetValue("limit", out var supplied) &&
            (supplied.Count != 1 || !int.TryParse(supplied[0], out limit) || limit is < 1 or > 20))
            return Results.Problem(statusCode: 400, title: "The page limit is invalid.");

        try
        {
            var page = await query.GetAsync(actor, caseId, limit, ct).ConfigureAwait(false);
            if (page is null) return NotFound();
            return Results.Json(new EligibleHealthOfficerCandidatePage(page.Items.Select(item =>
                new EligibleHealthOfficerCandidate(item.DesignationId, item.DesignationVersion, item.ActorId, item.DisplayLabel)).ToArray(), page.HasMore));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "Officer candidates are temporarily unavailable."); }
    }

    private static IResult NotFound() => Results.Problem(statusCode: 404, title: "The case was not found.");
}
