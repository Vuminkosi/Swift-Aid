using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Surveillance;

namespace SwiftAid.Gateway.Endpoints;

public static class SurveillanceSignalEndpoints
{
    public static void MapSurveillanceSignalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = new AuthorizeAttribute { AuthenticationSchemes = string.Join(",", IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) };
        endpoints.MapGet("/api/v1/cases/{caseId:guid}/signals", async (Guid caseId, HttpContext context, ISignalReviewService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!service.Enabled) return Results.NotFound();
            return Results.Ok(await service.ForCaseAsync(ActorContext.FromValidatedPrincipal(context.User), caseId, ct));
        }).RequireAuthorization(auth);
        endpoints.MapGet("/api/v1/signals/{signalId:guid}", async (Guid signalId, HttpContext context, ISignalReviewService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var result = await service.GetAsync(ActorContext.FromValidatedPrincipal(context.User), signalId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization(auth);
        endpoints.MapPost("/api/v1/signals/{signalId:guid}/decisions", async (Guid signalId, SignalDecisionCommand command,
            HttpContext context, ISignalReviewService service, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!service.Enabled) return Results.NotFound();
            if (signalId != command.SignalId) return Results.BadRequest();
            if (!(await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)).Succeeded)
            {
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            var result = await service.DecideAsync(command, ActorContext.FromValidatedPrincipal(context.User), ct);
            return result.Outcome switch
            {
                "Completed" => Results.Ok(result), "Conflict" => Results.Conflict(result),
                "Invalid" or "InvalidTransition" => Results.BadRequest(result), _ => Results.NotFound()
            };
        }).RequireAuthorization(auth).WithMetadata(new RequestSizeLimitAttribute(8 * 1024));
    }
}
