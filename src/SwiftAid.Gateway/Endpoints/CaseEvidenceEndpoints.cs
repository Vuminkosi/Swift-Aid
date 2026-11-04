using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Gateway.Endpoints;

public static class CaseEvidenceEndpoints
{
    public static void MapCaseEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/cases/{caseId:guid}").RequireAuthorization("SwiftAidStaffApi");
        group.MapGet("/observations", async (Guid caseId, int? page, int? pageSize, HttpContext http, CancellationToken ct) =>
            Json(await Observations(http).GetAsync(caseId, new(page ?? 1, pageSize ?? 25), ct), http));
        group.MapPost("/observations", async (Guid caseId, CaseObservationCommand command, HttpContext http, CancellationToken ct) =>
        {
            if (caseId != command.CaseId || !await ValidateWriteAsync(http)) return Results.BadRequest();
            return Json(await Observations(http).RecordAsync(command, ct), http);
        });
        group.MapGet("/evidence", async (Guid caseId, int? page, int? pageSize, HttpContext http, CancellationToken ct) =>
            Json(await Evidence(http).GetAsync(caseId, new(new(page ?? 1, pageSize ?? 25)), ct), http));
        group.MapGet("/evidence/{evidenceKey}", async (Guid caseId, string evidenceKey, HttpContext http, CancellationToken ct) =>
            Json(await Evidence(http).GetDetailAsync(caseId, evidenceKey, ct), http));
        group.MapGet("/evidence/{evidenceKey}/content", async (Guid caseId, string evidenceKey, HttpContext http, CancellationToken ct) =>
        {
            await using var buffer = new MemoryStream();
            var result = await Evidence(http).DownloadToAsync(caseId, evidenceKey, buffer, ct);
            http.Response.Headers.CacheControl = "no-store, private";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!result.Succeeded || result.Value is not { IsValid: true } metadata) return Json(result, http);
            http.Response.Headers["X-SwiftAid-Evidence"] = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(metadata));
            return Results.File(buffer.ToArray(), metadata.ContentType, metadata.FileName);
        });
        group.MapPost("/evidence/{submissionId:guid}", async (Guid caseId, Guid submissionId, long expectedVersion,
            string fileName, HttpContext http, CancellationToken ct) =>
        {
            if (!await ValidateWriteAsync(http) || http.Request.ContentLength is not (> 0 and <= CaseEvidenceUploadRequest.MaximumContentBytes))
                return Results.BadRequest();
            var request = new CaseEvidenceUploadRequest(caseId, submissionId, expectedVersion, fileName,
                http.Request.ContentType ?? "", http.Request.ContentLength.Value);
            return Json(await Evidence(http).UploadAsync(request, http.Request.Body, ct), http);
        });
    }

    private static CaseEvidenceGatewayClient Evidence(HttpContext http) => new(new RequestAuthentication(http),
        http.RequestServices.GetRequiredService<IServiceScopeFactory>(), http.RequestServices.GetRequiredService<ILogger<CaseEvidenceGatewayClient>>());
    private static CaseObservationGatewayClient Observations(HttpContext http) => new(new RequestAuthentication(http),
        http.RequestServices.GetRequiredService<IServiceScopeFactory>(), http.RequestServices.GetRequiredService<ILogger<CaseObservationGatewayClient>>());

    private static IResult Json<T>(UiCapabilityResult<T> result, HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        var status = result.Code switch
        {
            UiCapabilityResultCode.Success => 200,
            UiCapabilityResultCode.Denied => 404,
            UiCapabilityResultCode.InvalidRequest => 400,
            UiCapabilityResultCode.Conflict => 409,
            _ => 503
        };
        return Results.Json(result, statusCode: status);
    }

    private static async Task<bool> ValidateWriteAsync(HttpContext http)
    {
        if (!http.Request.Headers.ContainsKey("Cookie")) return true;
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private sealed class RequestAuthentication(HttpContext context) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(context.User));
    }
}
