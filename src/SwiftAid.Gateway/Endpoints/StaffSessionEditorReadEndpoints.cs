using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Gateway.Sessions;
using SwiftAid.Infrastructure.Identity;

namespace SwiftAid.Gateway.Endpoints;

public interface IStaffSessionEditorReadRequest
{
    Task<UiCapabilityResult<string>> ActorAsync(ClaimsPrincipal principal, CancellationToken ct);
    Task<UiCapabilityResult<StaffSessionManagementCapabilities>> CapabilitiesAsync(ClaimsPrincipal principal, Guid? sessionId, long? version, CancellationToken ct);
    Task<UiCapabilityResult<CommunitySessionView>> SessionAsync(ClaimsPrincipal principal, Guid sessionId, CancellationToken ct);
}

public sealed class StaffSessionEditorReadRequest(IServiceProvider services) : IStaffSessionEditorReadRequest
{
    private GatewayStaffSessionEditorClient Client(ClaimsPrincipal principal)
    {
        var authentication = new RequestAuthenticationStateProvider(principal);
        var session = new SessionGatewayClient(authentication, services);
        var membership = new GatewayOrganizationMembershipClient(authentication,
            services.GetRequiredService<UserManager<SwiftAidUser>>(),
            services.GetRequiredService<IOrganizationMembershipService>(),
            services.GetRequiredService<IOrganizationInvitationService>());
        return new GatewayStaffSessionEditorClient(session, membership);
    }

    public Task<UiCapabilityResult<string>> ActorAsync(ClaimsPrincipal principal, CancellationToken ct) => Client(principal).GetCurrentActorIdAsync(ct);
    public Task<UiCapabilityResult<StaffSessionManagementCapabilities>> CapabilitiesAsync(ClaimsPrincipal principal, Guid? sessionId, long? version, CancellationToken ct) =>
        Client(principal).GetManagementCapabilitiesAsync(sessionId, version, ct);
    public Task<UiCapabilityResult<CommunitySessionView>> SessionAsync(ClaimsPrincipal principal, Guid sessionId, CancellationToken ct) =>
        Client(principal).GetStaffAsync(sessionId, ct);
}

public static class StaffSessionEditorReadEndpoints
{
    public static void MapStaffSessionEditorReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = new AuthorizeAttribute { AuthenticationSchemes = string.Join(",", IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) };
        var group = endpoints.MapGroup("/api/v1/session-editor").RequireAuthorization(auth);
        group.MapGet("/actor", async (HttpContext context, IStaffSessionEditorReadRequest service, IWorkspaceAccessRequest access, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (await RejectStaleAsync(context, access, ct) is { } denied) return denied;
            return Map(await service.ActorAsync(context.User, ct));
        });
        group.MapGet("/capabilities", async (Guid? sessionId, long? version, HttpContext context, IStaffSessionEditorReadRequest service, IWorkspaceAccessRequest access, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sessionId == Guid.Empty || version is <= 0 || sessionId.HasValue != version.HasValue) return Results.BadRequest();
            if (await RejectStaleAsync(context, access, ct) is { } denied) return denied;
            return Map(await service.CapabilitiesAsync(context.User, sessionId, version, ct));
        });
        group.MapGet("/sessions/{sessionId:guid}", async (Guid sessionId, HttpContext context, IStaffSessionEditorReadRequest service, IWorkspaceAccessRequest access, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sessionId == Guid.Empty) return Results.BadRequest();
            if (await RejectStaleAsync(context, access, ct) is { } denied) return denied;
            return Map(await service.SessionAsync(context.User, sessionId, ct));
        });
    }

    private static async Task<IResult?> RejectStaleAsync(HttpContext context, IWorkspaceAccessRequest access, CancellationToken ct)
    {
        var current = await access.GetAsync(context.User, null, ct);
        return current.Succeeded ? null : Results.StatusCode(current.Code == UiCapabilityResultCode.Denied
            ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult Map<T>(UiCapabilityResult<T> result) => result.Code switch
    {
        UiCapabilityResultCode.Success when result.Value is not null => Results.Ok(result.Value),
        UiCapabilityResultCode.InvalidRequest => Results.BadRequest(),
        UiCapabilityResultCode.Denied => Results.NotFound(),
        _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
    };
}
