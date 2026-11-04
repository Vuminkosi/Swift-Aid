using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Gateway.Endpoints;

public static class InvestigationEndpoints
{
    public static void MapInvestigationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/investigations").RequireAuthorization("SwiftAidStaffApi");
        group.MapGet("", async (int? page, int? pageSize, HttpContext http, CancellationToken ct) => Json(await Client(http).ListAsync(new(page ?? 1, pageSize ?? 25), ct), http));
        group.MapGet("/{id}", async (string id, HttpContext http, CancellationToken ct) => Json(await Client(http).GetAsync(id, ct), http));
        group.MapGet("/{id}/execution", async (string id, HttpContext http, CancellationToken ct) => Json(await Client(http).GetExecutionAndVerificationAsync(id, ct), http));
        group.MapPost("", async (InvestigationRequest input, HttpContext http, CancellationToken ct) =>
            await VerifiedAsync(http) ? Json(await Client(http).StartAsync(input, ct), http) : Results.BadRequest()).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32768));
        Post<InvestigationVersionRequest>(group, "/{id}/pause", (client, id, input, ct) => client.PauseAsync(id, input.ExpectedVersion, ct));
        Post<InvestigationVersionRequest>(group, "/{id}/resume", (client, id, input, ct) => client.ResumeAsync(id, input.ExpectedVersion, ct));
        Post<InvestigationReplyRequest>(group, "/{id}/reply", (client, id, input, ct) => client.ReplyAsync(id, input.ExpectedVersion, input.ClarificationId, input.FactualAnswer, ct));
        Post<InvestigationProposalRequest>(group, "/{id}/propose", (client, id, input, ct) => client.ProposeAdministrativeActionAsync(id, input.ExpectedVersion, input.Summary, ct));
        Post<InvestigationProposalReview>(group, "/{id}/review", (client, id, input, ct) => id == input.InvestigationId
            ? client.ReviewProposalAsync(input, ct) : Task.FromResult(new UiCapabilityResult<InvestigationSnapshot>(UiCapabilityResultCode.InvalidRequest,
                null, UiCapabilityResults.UnavailableMetadata("investigations", "Invalid review."))));
    }
    private static void Post<T>(RouteGroupBuilder group, string route, Func<InvestigationGatewayClient, string, T, CancellationToken, Task<UiCapabilityResult<InvestigationSnapshot>>> execute) where T : class =>
        group.MapPost(route, async (string id, [Microsoft.AspNetCore.Mvc.FromBody] T input, HttpContext http, CancellationToken ct) =>
            await VerifiedAsync(http) ? Json(await execute(Client(http), id, input, ct), http) : Results.BadRequest())
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32768));
    private static async Task<bool> VerifiedAsync(HttpContext http)
    {
        if (!await CaseCommandEndpoints.RequiresAntiforgeryAsync(http)) return true;
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
    private static InvestigationGatewayClient Client(HttpContext http) => new(new RequestAuthentication(http),
        http.RequestServices.GetRequiredService<IServiceScopeFactory>(), http.RequestServices.GetRequiredService<IDataProtectionProvider>(),
        http.RequestServices.GetRequiredService<ILogger<InvestigationGatewayClient>>());
    private static IResult Json<T>(UiCapabilityResult<T> result, HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        return Results.Json(result, statusCode: result.Code switch
        { UiCapabilityResultCode.Success => 200, UiCapabilityResultCode.Denied => 404, UiCapabilityResultCode.InvalidRequest => 400, UiCapabilityResultCode.Conflict => 409, _ => 503 });
    }
    private sealed class RequestAuthentication(HttpContext http) : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(http.User)); }
}