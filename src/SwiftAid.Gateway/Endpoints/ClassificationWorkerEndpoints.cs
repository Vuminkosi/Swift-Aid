using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Infrastructure.Classification;

namespace SwiftAid.Gateway.Endpoints;

public static class ClassificationWorkerEndpoints
{
    public static IApplicationBuilder UseClassificationRequestLimit(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/internal/classification"))
        {
            var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 32768;
        }
        await next(context);
    });

    public static IEndpointRouteBuilder MapClassificationWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/internal/classification").AllowAnonymous();
        // Dedicated machine credential: user cookies and UI antiforgery tokens grant no worker authority.
        group.AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<ClassificationOptions>>().Value;
            if (!options.Enabled) return Results.NotFound();
            options.Validate();
            var header = context.HttpContext.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length is < 39 or > 256 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])), Convert.FromHexString(options.WorkerTokenSha256)))
                return Results.Unauthorized();
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (context.HttpContext.Request.ContentLength > 32768) return Results.StatusCode(413);
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (ArgumentException) { return Results.BadRequest(new { code = "invalid-classification-contract" }); }
            catch (InvalidDataException) { return Results.BadRequest(new { code = "invalid-source-journal" }); }
        });
        group.MapPost("/claim", async (ClassificationClaimRequest request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(await registry.ClaimAsync(request.Notice, ct)));
        group.MapPost("/complete", async (ClassificationCompletion request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(new { state = await registry.CompleteAsync(request, ct) }));
        group.MapPost("/fail", async (ClassificationFailure request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(new { state = await registry.FailAsync(request, ct) }));
        group.MapPost("/renew", async (ClassificationRenewal request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(new { state = await registry.RenewAsync(request, ct) }));
        group.MapPost("/quarantine", async (ClassificationQuarantine request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(new { state = await registry.QuarantineAsync(request, ct) }));
        return endpoints;
    }
}
