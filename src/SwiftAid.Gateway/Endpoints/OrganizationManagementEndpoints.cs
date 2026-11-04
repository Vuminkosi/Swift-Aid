using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Presentation;

namespace SwiftAid.Gateway.Endpoints;

public static class OrganizationManagementEndpoints
{
    public static void MapOrganizationManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/organizations/{organizationId}/management").RequireAuthorization("SwiftAidStaffApi");
        group.MapGet("/profile", async (string organizationId, HttpContext http, CancellationToken ct) => Json(await Client(http, organizationId).GetProfileAsync(ct), http));
        group.MapGet("/members", async (string organizationId, int? page, int? pageSize, HttpContext http, CancellationToken ct) => Json(await Client(http, organizationId).ListManagedMembersAsync(new(page ?? 1, pageSize ?? 25), ct), http));
        group.MapGet("/requests", async (string organizationId, int? page, int? pageSize, HttpContext http, CancellationToken ct) => Json(await Client(http, organizationId).ListMembershipRequestsAsync(new(page ?? 1, pageSize ?? 25), ct), http));
        group.MapGet("/sso", async (string organizationId, HttpContext http, CancellationToken ct) => Json(await Client(http, organizationId).GetSsoConfigurationAsync(ct), http));
        group.MapGet("/audit", async (string organizationId, HttpContext http, CancellationToken ct) => Json(await Client(http, organizationId).GetAuditAsync(ct), http));
        Post<OrganizationProfileUpdate, OrganizationProfile>(group, "/profile", (client, input, ct) => client.UpdateProfileAsync(input, ct));
        Post<OrganizationMembershipRequestSubmission, OrganizationMembershipRequest>(group, "/requests", (client, input, ct) => client.SubmitMembershipRequestAsync(input, ct));
        Post<OrganizationMembershipRequestReview, OrganizationMembershipRequest>(group, "/requests/review", (client, input, ct) => client.ReviewMembershipRequestAsync(input, ct));
        Post<OrganizationMemberChange, ManagedOrganizationMember>(group, "/members", (client, input, ct) => client.UpdateMemberAsync(input, ct));
        Post<OrganizationMembershipRevocation, ManagedOrganizationMember>(group, "/members/revoke", (client, input, ct) => client.RevokeMembershipAsync(input, ct));
        Post<OrganizationInvitationRequest, OrganizationInvitation>(group, "/invitations", (client, input, ct) => client.SendInvitationAsync(input, ct));
        Post<OrganizationSsoSetupRequest, OrganizationSsoConfiguration>(group, "/sso", (client, input, ct) => client.RequestSsoSetupAsync(input.Reason, ct));
    }

    private static void Post<TInput, TOutput>(RouteGroupBuilder group, string route,
        Func<OrganizationManagementGatewayClient, TInput, CancellationToken, Task<UiCapabilityResult<TOutput>>> execute) where TInput : class
    {
        group.MapPost(route, async (string organizationId, [Microsoft.AspNetCore.Mvc.FromBody] TInput input, HttpContext http, CancellationToken ct) =>
        {
            if (await CaseCommandEndpoints.RequiresAntiforgeryAsync(http))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            return Json(await execute(Client(http, organizationId), input, ct), http);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16384));
    }
    private static OrganizationManagementGatewayClient Client(HttpContext http, string organizationId) => new(new RequestAuthentication(http),
        http.RequestServices.GetRequiredService<IServiceScopeFactory>(), new() { OrganizationId = organizationId },
        http.RequestServices.GetRequiredService<IDataProtectionProvider>(), http.RequestServices.GetRequiredService<ILogger<OrganizationManagementGatewayClient>>());
    private static IResult Json<T>(UiCapabilityResult<T> result, HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        return Results.Json(result, statusCode: result.Code switch
        { UiCapabilityResultCode.Success => 200, UiCapabilityResultCode.Denied => 404, UiCapabilityResultCode.InvalidRequest => 400, UiCapabilityResultCode.Conflict => 409, _ => 503 });
    }
    private sealed class RequestAuthentication(HttpContext http) : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(http.User)); }
}