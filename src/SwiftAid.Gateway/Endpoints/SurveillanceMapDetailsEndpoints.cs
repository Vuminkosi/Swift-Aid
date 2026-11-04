using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Surveillance;
using SwiftAid.Gateway.Cases;
using SwiftAid.Gateway.Presentation;

namespace SwiftAid.Gateway.Endpoints;

public interface ISurveillanceMapDetailsRequest
{
    Task<UiCapabilityResult<SurveillanceMapDetails>> GetAsync(ClaimsPrincipal principal, string country,
        MapAggregatePeriod period, string? workspace, CancellationToken cancellationToken);
}

/// <summary>Creates the same authorized projection used by the web circuit for the current HTTP principal.</summary>
public sealed class SurveillanceMapDetailsRequest(IServiceScopeFactory scopes, TimeProvider clock,
    ILoggerFactory loggers) : ISurveillanceMapDetailsRequest
{
    public async Task<UiCapabilityResult<SurveillanceMapDetails>> GetAsync(ClaimsPrincipal principal, string country,
        MapAggregatePeriod period, string? workspace, CancellationToken cancellationToken)
    {
        var authentication = new RequestAuthenticationStateProvider(principal);
        var reports = new CaseReportDetailsGatewayClient(authentication, scopes, loggers.CreateLogger<CaseReportDetailsGatewayClient>());
        var workspaces = new GatewayWorkspaceAccessClient(authentication, scopes, clock, loggers.CreateLogger<GatewayWorkspaceAccessClient>());
        if (workspace is not null)
        {
            var selection = await workspaces.SelectWorkspaceAsync(workspace, cancellationToken);
            if (!selection.Succeeded)
                return new(selection.Code, null, selection.Metadata, selection.Message);
        }
        return await new SurveillanceMapDetailsGatewayClient(reports, workspaces, scopes, authentication,
            loggers.CreateLogger<SurveillanceMapDetailsGatewayClient>()).GetAsync(country, period, cancellationToken);
    }
}

public static class SurveillanceMapDetailsEndpoints
{
    public static void MapSurveillanceMapDetailsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = new AuthorizeAttribute { AuthenticationSchemes = string.Join(",", IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) };
        endpoints.MapGet("/api/v1/surveillance/map-details", async (string? country, string? period, string? workspace, HttpContext context,
            ISurveillanceMapDetailsRequest service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (country is not { Length: 2 } || !country.All(char.IsAsciiLetter) ||
                period is not ("days30" or "days90") ||
                workspace is not null && (string.IsNullOrWhiteSpace(workspace) || workspace.Length > 256)) return Results.BadRequest();
            var result = await service.GetAsync(context.User, country.ToUpperInvariant(),
                period == "days90" ? MapAggregatePeriod.Days90 : MapAggregatePeriod.Days30, workspace, ct);
            return result.Succeeded && result.Value is not null
                ? Results.Ok(result.Value)
                : Results.StatusCode(result.Code == UiCapabilityResultCode.Denied ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization(auth);
    }
}
