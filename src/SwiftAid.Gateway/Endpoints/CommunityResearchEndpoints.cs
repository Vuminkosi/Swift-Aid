using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.CommunityResearch;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Accepts owner-attested aggregate counts, never individual feedback.</summary>
public static class CommunityResearchEndpoints
{
    public static void MapCommunityResearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var schemes = string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        endpoints.MapPost("/api/v1/community/research/feedback-aggregates", SubmitAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(2048));
        endpoints.MapPost("/api/v1/community/research/feedback-aggregates/{aggregateId:guid}/review", ReviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(2048));
    }

    private static async Task<IResult> ReviewAsync(HttpContext context, Guid aggregateId, IAntiforgery antiforgery, CancellationToken ct)
    {
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var service = context.RequestServices.GetService<ICommunityFeedbackAggregateReview>();
        if (service is null) return Results.Problem(statusCode: 404, title: "The aggregate was not found.");
        if (await CaseCommandEndpoints.RequiresAntiforgeryAsync(context).ConfigureAwait(false))
        {
            try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
            catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Request verification failed."); }
        }
        if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 400, title: "The review request is invalid.");
        const int maximumBytes = 2048;
        if (context.Request.ContentLength > maximumBytes) return Results.Problem(statusCode: 413, title: "The review request is too large.");
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > maximumBytes) return Results.Problem(statusCode: 413, title: "The review request is too large.");
        CommunityAggregateReviewRequest? request;
        try { request = JsonSerializer.Deserialize<CommunityAggregateReviewRequest>(buffer.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return Results.Problem(statusCode: 400, title: "The review request is invalid."); }
        if (request is null) return Results.Problem(statusCode: 400, title: "The review request is invalid.");
        try
        {
            var outcome = await service.ReviewAsync(actor, aggregateId, request, ct).ConfigureAwait(false);
            return outcome switch
            {
                OrganizationDecisionOutcome.NotFoundOrNotAuthorized => Results.Problem(statusCode: 404, title: "The aggregate was not found."),
                OrganizationDecisionOutcome.Invalid => Results.Problem(statusCode: 400, title: "The review request is invalid or self-review was not acknowledged."),
                OrganizationDecisionOutcome.Conflict => Results.Problem(statusCode: 409, title: "The review conflicts with an earlier request."),
                _ => Results.Json(new { status = outcome.ToString() })
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "The review could not be recorded."); }
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, IAntiforgery antiforgery, CancellationToken ct)
    {
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var service = context.RequestServices.GetService<ICommunityFeedbackAggregateSubmission>();
        if (service is null) return Results.Problem(statusCode: 404, title: "The session was not found.");
        if (await CaseCommandEndpoints.RequiresAntiforgeryAsync(context).ConfigureAwait(false))
        {
            try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
            catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Request verification failed."); }
        }
        if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 400, title: "The aggregate request is invalid.");
        const int maximumBytes = 2048;
        if (context.Request.ContentLength > maximumBytes) return Results.Problem(statusCode: 413, title: "The aggregate request is too large.");
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > maximumBytes) return Results.Problem(statusCode: 413, title: "The aggregate request is too large.");
        CommunityFeedbackAggregate? request;
        try { request = JsonSerializer.Deserialize<CommunityFeedbackAggregate>(buffer.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return Results.Problem(statusCode: 400, title: "The aggregate request is invalid."); }
        if (request is null) return Results.Problem(statusCode: 400, title: "The aggregate request is invalid.");
        try
        {
            var outcome = await service.SubmitAsync(actor, request, ct).ConfigureAwait(false);
            return outcome switch
            {
                CommunityAggregateSubmissionStatus.Recorded or CommunityAggregateSubmissionStatus.Replayed => Results.Json(new { status = outcome.ToString(), approved = false }),
                CommunityAggregateSubmissionStatus.Invalid => Results.Problem(statusCode: 400, title: "The aggregate request is invalid."),
                CommunityAggregateSubmissionStatus.Conflict => Results.Problem(statusCode: 409, title: "The aggregate conflicts with an earlier submission."),
                _ => Results.Problem(statusCode: 404, title: "The session was not found.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "The aggregate could not be recorded."); }
    }
}
