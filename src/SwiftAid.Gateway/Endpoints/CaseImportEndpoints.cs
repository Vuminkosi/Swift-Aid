using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Gateway.Ingestion;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Authenticated staff-only bounded CSV batches; no public-intake capability is promoted to staff authority.</summary>
public static class CaseImportEndpoints
{
    private const int MaximumBatchBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    public static void MapCaseImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/v1/case-imports").RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = IdentityConstants.ApplicationScheme + "," + OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
        });
        routes.MapPost("/{importId:guid}/rows", ImportAsync).WithMetadata(new RequestSizeLimitAttribute(MaximumBatchBytes));
        routes.MapGet("/{importId:guid}", StatusAsync);
    }

    private static async Task<IResult> ImportAsync(Guid importId, long startRow, bool finalBatch,
        HttpContext http, IAntiforgery antiforgery, CaseCsvImportService importer, CancellationToken ct)
    {
        try
        {
            var actor = ActorContext.FromValidatedPrincipal(http.User);
            if (http.Request.ContentLength > MaximumBatchBytes)
                return Results.Problem(statusCode: 413, title: "The import batch is too large.");
            if (await ManagedCaseRegistrationEndpoints.RequiresAntiforgeryAsync(http))
                await antiforgery.ValidateRequestAsync(http);
            if (!string.Equals(http.Request.ContentType?.Split(';')[0], "text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.Problem(statusCode: 415, title: "A UTF-8 CSV batch is required.");
            var metadata = http.Request.Headers["X-SwiftAid-Import"].ToString();
            if (metadata.Length is < 1 or > 8192 || startRow < 1 || importId == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "The import context is invalid.");
            var context = JsonSerializer.Deserialize<CaseImportContext>(Convert.FromBase64String(metadata), Json)
                ?? throw new JsonException();
            using var bounded = new CaseImportRequestStream(http.Request.Body, MaximumBatchBytes);
            using var reader = new StreamReader(bounded, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var result = await importer.ProcessAsync(actor, importId, context, startRow, finalBatch, reader, ct);
            return Results.Json(result, statusCode: result.ErrorCode is null ? 200 : result.ErrorCode == "batch-byte-limit" ? 413 : 422);
        }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "The request token is invalid."); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (CaseImportException exception) { return Failure(exception.Code); }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidDataException or DecoderFallbackException)
        { return Results.Problem(statusCode: 400, title: "The import batch is invalid."); }
    }

    private static async Task<IResult> StatusAsync(Guid importId, HttpContext http, ICaseImportStore store, CancellationToken ct)
    {
        try
        {
            var summary = await store.GetAsync(ActorContext.FromValidatedPrincipal(http.User), importId, ct);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (CaseImportException exception) { return Failure(exception.Code); }
    }

    private static IResult Failure(string code) => Results.Problem(
        statusCode: code == "batch-byte-limit" ? 413 : code == "not-authorized" ? 403 : code is "import-busy" or "import-context-conflict" or "checkpoint-gap" or "import-completed" ? 409 : 400,
        title: "The import could not proceed.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
