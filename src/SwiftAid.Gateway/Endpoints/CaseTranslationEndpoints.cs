using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Classification;
using SwiftAid.Infrastructure.Classification;

namespace SwiftAid.Gateway.Endpoints;

public static class CaseTranslationEndpoints
{
    public static void MapCaseTranslationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = new AuthorizeAttribute { Policy = "SwiftAidStaffApi" };
        endpoints.MapGet("/api/v1/cases/{caseId:guid}/translations", async (Guid caseId, HttpContext context,
            CaseTranslationService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var result = await service.ReadAsync(ActorContext.FromValidatedPrincipal(context.User), caseId, ct);
                return result is null ? Results.NotFound() : Results.Ok(new { caseId, items = result });
            }
            catch (UnauthorizedAccessException) { return Results.NotFound(); }
        }).RequireAuthorization(auth);
        endpoints.MapPost("/api/v1/cases/{caseId:guid}/translations/{artifactId:guid}/reviews", async (Guid caseId, Guid artifactId,
            TranslationReviewRequest request, HttpContext context, CaseTranslationService service, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!(await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)).Succeeded)
            {
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            try
            {
                var state = await service.ReviewAsync(ActorContext.FromValidatedPrincipal(context.User), caseId, artifactId, request, ct);
                return state == "Conflict" ? Results.Conflict(new { state }) : Results.Ok(new { state });
            }
            catch (UnauthorizedAccessException) { return Results.NotFound(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireAuthorization(auth).WithMetadata(new RequestSizeLimitAttribute(8192));
    }
}
