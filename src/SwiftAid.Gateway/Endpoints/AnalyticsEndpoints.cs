using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Gateway.Endpoints;

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var authorization = new AuthorizeAttribute { AuthenticationSchemes = string.Join(",", IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) };
        endpoints.MapGet("/api/v1/analytics/work-summary", async (HttpContext context, int? page, int? pageSize,
            string? period, string? region, string? workType, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (page is <= 0 or > 100_000 || pageSize is <= 0 or > 100 ||
                region?.Length > 128 || workType?.Length > 32 || period?.Length > 8)
                return Results.BadRequest();

            var query = new AnalyticsQuery(new PageRequest(page ?? 1, pageSize ?? 25), period ?? "30", region, workType);
            var client = new AnalyticsGatewayClient(new RequestAuthenticationStateProvider(context.User), context.RequestServices);
            var result = await client.GetAsync(query, cancellationToken);
            return result.Code switch
            {
                UiCapabilityResultCode.Success when result.Value is not null => Results.Ok(result),
                UiCapabilityResultCode.InvalidRequest => Results.BadRequest(),
                UiCapabilityResultCode.Denied => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            };
        }).RequireAuthorization(authorization);
    }
}
