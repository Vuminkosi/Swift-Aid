using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.HealthEvidence;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Approved public reference metadata only; no source content, advice or publication.</summary>
public static class HealthSourceEndpoints
{
    public static void MapHealthSourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/health-sources/search", SearchAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" })
            .WithMetadata(new RequestSizeLimitAttribute(2048));
    }

    private static async Task<IResult> SearchAsync(HttpContext context, IApprovedHealthSourceRegistry registry, IAntiforgery antiforgery, CancellationToken ct)
    {
        try { _ = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        if (await CaseCommandEndpoints.RequiresAntiforgeryAsync(context).ConfigureAwait(false))
        {
            try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
            catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Request verification failed."); }
        }
        if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 400, title: "The source search is invalid.");
        const int maximumBytes = 2048;
        if (context.Request.ContentLength > maximumBytes) return Results.Problem(statusCode: 413, title: "The source search is too large.");
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > maximumBytes) return Results.Problem(statusCode: 413, title: "The source search is too large.");
        HealthSourceSearchRequest? request;
        try { request = JsonSerializer.Deserialize<HealthSourceSearchRequest>(buffer.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return Results.Problem(statusCode: 400, title: "The source search is invalid."); }
        if (request is null) return Results.Problem(statusCode: 400, title: "The source search is invalid.");
        try
        {
            var result = await registry.SearchAsync(request.Topic, request.Jurisdiction, request.Language, ct).ConfigureAwait(false);
            return result.IsConfigured ? Results.Json(result) : Results.Problem(statusCode: 503, title: "Approved sources are not configured.");
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "A topic, jurisdiction and language are required."); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "Approved sources are temporarily unavailable."); }
    }

    private sealed record HealthSourceSearchRequest(string Topic, string Jurisdiction, string Language);
}
