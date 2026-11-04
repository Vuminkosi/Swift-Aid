using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;
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
        group.MapGet("/ready", async (SwiftAidDbContext db, CancellationToken ct) =>
        {
            // Authenticated readiness checks the persistence boundary without exposing case data.
            await db.Set<ClassificationResultEntity>().AnyAsync(ct);
            return Results.Ok(new { status = "ready" });
        });
        group.MapPost("/claim", async (ClassificationClaimRequest request, ClassificationRegistry registry, ClassificationBackfillStore backfill, CancellationToken ct) =>
        {
            if (request.BackfillJobId is { } id) await backfill.ValidateNoticeAsync(id, request.Notice.MessageId, ct);
            var claim = await registry.ClaimAsync(request.Notice, ct);
            if (request.BackfillJobId is { } job) await backfill.BindResultAsync(job, request.Notice.MessageId, claim.ConfigurationHash!, ct, claim.State);
            return Results.Ok(claim);
        });
        group.MapPost("/complete", async (ClassificationCompletion request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(new { state = await registry.CompleteAsync(request, ct) }));
        group.MapPost("/fail", async (ClassificationFailure request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(new { state = await registry.FailAsync(request, ct) }));
        group.MapPost("/renew", async (ClassificationRenewal request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(new { state = await registry.RenewAsync(request, ct) }));
        group.MapPost("/quarantine", async (ClassificationQuarantine request, ClassificationRegistry registry, CancellationToken ct) =>
            Results.Ok(new { state = await registry.QuarantineAsync(request, ct) }));
        group.MapPost("/backfills", async (StartClassificationBackfill request, ClassificationBackfillStore store, CancellationToken ct) =>
            Results.Ok(await store.StartAsync(request, ct)));
        group.MapGet("/backfills/runnable", async (ClassificationBackfillStore store, CancellationToken ct) =>
        {
            var id = await store.FindRunnableAsync(ct);
            return Results.Ok(new { jobId = id });
        });
        group.MapGet("/backfills/{id:guid}", async (Guid id, ClassificationBackfillStore store, CancellationToken ct) =>
            Results.Ok(await store.StatusAsync(id, ct)));
        group.MapPost("/backfills/{id:guid}/next", async (Guid id, ClassificationBackfillStore store, CancellationToken ct) =>
            Results.Ok(await store.NextAsync(id, ct)));
        group.MapPost("/backfills/{id:guid}/control", async (Guid id, ClassificationBackfillControl request, ClassificationBackfillStore store, CancellationToken ct) =>
            Results.Ok(await store.ControlAsync(id, request.Paused, ct)));
        return endpoints;
    }
}
