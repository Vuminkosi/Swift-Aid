using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Gateway.Endpoints;

public static class FollowUpReviewEndpoints
{
    public static void MapFollowUpReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var schemes = string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        endpoints.MapPost("/api/v1/cases/{caseId:guid}/follow-up-recommendations/{recommendationId:guid}/review", ReviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(2048));
    }

    private static async Task<IResult> ReviewAsync(HttpContext context, Guid caseId, Guid recommendationId,
        IAntiforgery antiforgery, CancellationToken ct)
    {
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var service = context.RequestServices.GetService<IFollowUpRecommendationReviewService>();
        if (service is null) return Results.Problem(statusCode: 404, title: "The case was not found.");
        if (await CaseCommandEndpoints.RequiresAntiforgeryAsync(context).ConfigureAwait(false))
        {
            try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
            catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Request verification failed."); }
        }
        if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 400, title: "The review request is invalid.");
        const int maximumBytes = 2048;
        if (context.Request.ContentLength > maximumBytes)
            return Results.Problem(statusCode: 413, title: "The review request is too large.");
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > maximumBytes) return Results.Problem(statusCode: 413, title: "The review request is too large.");
        FollowUpReviewRequest? request;
        try { request = JsonSerializer.Deserialize<FollowUpReviewRequest>(buffer.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return Results.Problem(statusCode: 400, title: "The review request is invalid."); }
        if (caseId == Guid.Empty || recommendationId == Guid.Empty || request is null || request.CommandId == Guid.Empty)
            return Results.Problem(statusCode: 400, title: "The review request is invalid.");
        try
        {
            var outcome = await service.ReviewAsync(actor, caseId, recommendationId, request.CommandId, ct).ConfigureAwait(false);
            if (outcome == FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized)
                return Results.Problem(statusCode: 404, title: "The case was not found.");
            if (outcome == FollowUpRecommendationReviewOutcome.Conflict)
                return Results.Problem(statusCode: 409, title: "The review request conflicts with an earlier request.");
            return Results.Json(new FollowUpReviewResult(FollowUpReviewGatewayClient.Map(outcome)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "The review is temporarily unavailable."); }
    }
}
