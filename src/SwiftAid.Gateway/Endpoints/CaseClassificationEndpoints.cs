using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Classification;

namespace SwiftAid.Gateway.Endpoints;

public static class CaseClassificationEndpoints
{
    public static IEndpointRouteBuilder MapCaseClassificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/cases/{caseId:guid}/classification", async (Guid caseId, HttpContext context, ICaseClassificationQuery query, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var actor = ActorContext.FromValidatedPrincipal(context.User);
                var result = await query.GetAsync(actor, caseId, ct);
                return result is null ? Results.NotFound() : Results.Ok(new { caseId, items = result, interpretation = "Reported semantic patterns; not a diagnosis or calibrated disease probability." });
            }
            catch (UnauthorizedAccessException) { return Results.NotFound(); }
        }).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = string.Join(",", IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) });
        return endpoints;
    }
}
