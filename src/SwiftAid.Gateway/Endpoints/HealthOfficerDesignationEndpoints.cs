using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Controlled role designation, independent of case permissions and professional licensure.</summary>
public static class HealthOfficerDesignationEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapHealthOfficerDesignationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var schemes = string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        endpoints.MapPost("/api/v1/health-officer-designations/changes", ChangeAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(2048));
    }

    private static async Task<IResult> ChangeAsync(HttpContext context, IAntiforgery antiforgery, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(context.User); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 401, title: "Authentication is required."); }
        var service = context.RequestServices.GetService<IHealthOfficerDesignationAdministration>();
        if (service is null) return Results.Problem(statusCode: 503, title: "Designation administration is unavailable.");
        if (await CaseCommandEndpoints.RequiresAntiforgeryAsync(context).ConfigureAwait(false))
        {
            try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
            catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Request verification failed."); }
        }
        if (!context.Request.HasJsonContentType()) return Invalid();
        const int maxBytes = 2048;
        if (context.Request.ContentLength > maxBytes) return TooLarge();
        var buffer = new byte[maxBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > maxBytes) return TooLarge();
        HealthOfficerDesignationChangeRequest? request;
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) return Invalid();
            request = document.RootElement.Deserialize<HealthOfficerDesignationChangeRequest>(JsonOptions);
        }
        catch (JsonException) { return Invalid(); }
        if (request is null) return Invalid();
        try
        {
            var result = await service.ChangeAsync(actor, new(request.CommandId, request.TargetActorId, request.OrganizationId,
                request.CountryCode, request.Sector, request.Status, request.ValidFromUtc, request.ExpiresAtUtc, request.ExpectedVersion), ct).ConfigureAwait(false);
            return result.Status switch
            {
                HealthOfficerDesignationChangeStatus.Recorded or HealthOfficerDesignationChangeStatus.Replayed
                    when result.DesignationId is { } id && id != Guid.Empty && result.Version is > 0 =>
                    Results.Json(new HealthOfficerDesignationChangeReceipt(id, result.Version.Value, result.Status == HealthOfficerDesignationChangeStatus.Replayed)),
                HealthOfficerDesignationChangeStatus.Denied => Results.Problem(statusCode: 404, title: "The designation scope was not found."),
                HealthOfficerDesignationChangeStatus.Conflict => Results.Problem(statusCode: 409, title: "The designation changed or the command conflicts with an earlier request."),
                HealthOfficerDesignationChangeStatus.Invalid => Invalid(),
                _ => Results.Problem(statusCode: 503, title: "Designation administration is unavailable.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Results.Problem(statusCode: 503, title: "Designation administration is unavailable."); }
    }

    private static IResult Invalid() => Results.Problem(statusCode: 400, title: "The designation request is invalid.");
    private static IResult TooLarge() => Results.Problem(statusCode: 413, title: "The designation request is too large.");
}
