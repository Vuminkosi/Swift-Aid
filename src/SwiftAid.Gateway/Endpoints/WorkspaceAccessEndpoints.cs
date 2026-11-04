using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Presentation;

namespace SwiftAid.Gateway.Endpoints;

public interface IWorkspaceAccessRequest
{
    Task<UiCapabilityResult<WorkspaceAccessState>> GetAsync(ClaimsPrincipal principal, string? workspace, CancellationToken ct);
}

public sealed class WorkspaceAccessRequest(IServiceScopeFactory scopes, TimeProvider clock, ILoggerFactory loggers) : IWorkspaceAccessRequest
{
    public Task<UiCapabilityResult<WorkspaceAccessState>> GetAsync(ClaimsPrincipal principal, string? workspace, CancellationToken ct)
    {
        var client = new GatewayWorkspaceAccessClient(new RequestAuthenticationStateProvider(principal), scopes, clock,
            loggers.CreateLogger<GatewayWorkspaceAccessClient>());
        return workspace is null ? client.GetStateAsync(ct) : client.SelectWorkspaceAsync(workspace, ct);
    }
}

internal sealed class RequestAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
}

public static class WorkspaceAccessEndpoints
{
    public static void MapWorkspaceAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = new AuthorizeAttribute { AuthenticationSchemes = string.Join(",", IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) };
        endpoints.MapGet("/api/v1/me/workspaces", async (string? workspace, HttpContext context,
            IWorkspaceAccessRequest service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (workspace is not null && (string.IsNullOrWhiteSpace(workspace) || workspace.Length > 256)) return Results.BadRequest();
            var result = await service.GetAsync(context.User, workspace, ct);
            return result.Code switch
            {
                UiCapabilityResultCode.Success when result.Value is not null => Results.Ok(result.Value),
                UiCapabilityResultCode.Denied => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            };
        }).RequireAuthorization(auth);
    }
}
