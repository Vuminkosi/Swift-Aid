using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Correlation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Gateway.Endpoints;

public static class CorrelationDecisionEndpoints
{
    public static void MapCorrelationDecisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/cases/{caseId:guid}/correlation-candidates/{recommendationId:guid}/decision", ReviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" })
            .WithMetadata(new RequestSizeLimitAttribute(2048));
    }

    private static async Task<IResult> ReviewAsync(HttpContext context, Guid caseId, Guid recommendationId,
        IAntiforgery antiforgery, CancellationToken ct)
    {
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var service = context.RequestServices.GetService<ICaseCorrelationDecisionService>();
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
        OrganizationDecisionRequest? request;
        try { request = JsonSerializer.Deserialize<OrganizationDecisionRequest>(buffer.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return Results.Problem(statusCode: 400, title: "The review request is invalid."); }
        if (caseId == Guid.Empty || recommendationId == Guid.Empty || request is null || request.CommandId == Guid.Empty)
            return Results.Problem(statusCode: 400, title: "The review request is invalid.");
        try
        {
            var outcome = await service.DecideAsync(actor, caseId, recommendationId, request.CommandId, request.Decision, request.Rationale, ct).ConfigureAwait(false);
            if (outcome == OrganizationDecisionOutcome.NotFoundOrNotAuthorized)
                return Results.Problem(statusCode: 404, title: "The case was not found.");
            if (outcome == OrganizationDecisionOutcome.Conflict)
                return Results.Problem(statusCode: 409, title: "The review request conflicts with an earlier request.");
            return Results.Json(new OrganizationDecisionResult(outcome));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "The review is temporarily unavailable."); }
    }
}
