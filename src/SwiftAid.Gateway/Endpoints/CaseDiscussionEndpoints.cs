using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Gateway.Endpoints;

public static class CaseDiscussionEndpoints
{
    public static void MapCaseDiscussionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/cases/{caseId:guid}/discussion").RequireAuthorization("SwiftAidStaffApi");
        group.MapGet("", async (Guid caseId, int? page, int? pageSize, HttpContext http, CancellationToken ct) =>
            Json(await Client(http).GetAsync(caseId, new(new(page ?? 1, pageSize ?? 25)), ct), http));
        group.MapGet("/mentions", async (Guid caseId, HttpContext http, CancellationToken ct) =>
            Json(await Client(http).GetMentionTargetsAsync(caseId, ct), http));
        group.MapPost("", async (Guid caseId, CaseDiscussionPostCommand command, HttpContext http, CancellationToken ct) =>
        {
            if (caseId != command.CaseId) return Results.BadRequest();
            if (await CaseCommandEndpoints.RequiresAntiforgeryAsync(http))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            return Json(await Client(http).AppendAsync(command, ct), http);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32768));
    }

    private static CaseDiscussionGatewayClient Client(HttpContext http) => new(new RequestAuthentication(http),
        http.RequestServices.GetRequiredService<IServiceScopeFactory>(), http.RequestServices.GetRequiredService<ILogger<CaseDiscussionGatewayClient>>());

    private static IResult Json<T>(UiCapabilityResult<T> result, HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        return Results.Json(result, statusCode: result.Code switch
        {
            UiCapabilityResultCode.Success => 200,
            UiCapabilityResultCode.Denied => 404,
            UiCapabilityResultCode.InvalidRequest => 400,
            UiCapabilityResultCode.Conflict => 409,
            _ => 503
        });
    }

    private sealed class RequestAuthentication(HttpContext http) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(http.User));
    }
}