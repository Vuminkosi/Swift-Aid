using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Endpoints;

public static class StaffSessionDraftEndpoints
{
    private const string Purpose = "SwiftAid.CommunitySession.DraftReservation.v1";
    private sealed record ReservationPayload(Guid SessionId, string ActorId, string OrganizationId);
    public sealed record ReserveRequest(string? OrganizationId);
    public sealed record CreateRequest(Guid SessionId, string? ReservationToken, CreateSessionApiRequest? Draft);

    public static void MapStaffSessionDraftEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = new AuthorizeAttribute { Policy = "SwiftAidStaffApi" };
        var group = endpoints.MapGroup("/api/v1/session-editor").RequireAuthorization(auth);
        group.MapPost("/draft-reservations", ReserveAsync).WithMetadata(new RequestSizeLimitAttribute(4096));
        group.MapPost("/drafts", CreateAsync).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
    }

    private static async Task<IResult> ReserveAsync(ReserveRequest? request, HttpContext context,
        IStaffSessionEditorReadRequest editor, IWorkspaceAccessRequest access, IDataProtectionProvider protection, IAntiforgery antiforgery, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (!await VerifyAsync(context, antiforgery)) return Results.BadRequest();
        var organization = request?.OrganizationId;
        if (string.IsNullOrWhiteSpace(organization) || organization.Length > 256) return Results.BadRequest();
        var current = await access.GetAsync(context.User, null, ct);
        if (!current.Succeeded) return Results.StatusCode(current.Code == UiCapabilityResultCode.Denied ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable);
        var actor = await editor.ActorAsync(context.User, ct);
        var capabilities = await editor.CapabilitiesAsync(context.User, null, null, ct);
        if (actor.Code == UiCapabilityResultCode.Unavailable || capabilities.Code == UiCapabilityResultCode.Unavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!actor.Succeeded || string.IsNullOrWhiteSpace(actor.Value) || !capabilities.Succeeded ||
            capabilities.Value is not { CanCreate: true, IsCurrent: true } caps ||
            caps.OwnerOrganizationIds?.Contains(organization, StringComparer.Ordinal) != true) return Results.NotFound();
        var payload = new ReservationPayload(Guid.NewGuid(), actor.Value, organization);
        var token = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(payload), TimeSpan.FromMinutes(30));
        return Results.Ok(new SessionDraftReservation(payload.SessionId, token));
    }

    private static async Task<IResult> CreateAsync(CreateRequest? request, HttpContext context,
        IStaffSessionEditorReadRequest editor, IWorkspaceAccessRequest access, IDataProtectionProvider protection, IAntiforgery antiforgery, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (!await VerifyAsync(context, antiforgery)) return Results.BadRequest();
        if (request is null || request.SessionId == Guid.Empty || request.ReservationToken is not { Length: > 0 and <= 4096 } || request.Draft is null)
            return Results.BadRequest();
        ReservationPayload? reservation;
        try
        {
            var json = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(request.ReservationToken);
            reservation = JsonSerializer.Deserialize<ReservationPayload>(json);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return Results.NotFound();
        }
        var current = await access.GetAsync(context.User, null, ct);
        if (!current.Succeeded) return Results.StatusCode(current.Code == UiCapabilityResultCode.Denied ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable);
        var actor = await editor.ActorAsync(context.User, ct);
        if (actor.Code == UiCapabilityResultCode.Unavailable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!actor.Succeeded || reservation is null || reservation.SessionId != request.SessionId ||
            !string.Equals(actor.Value, reservation.ActorId, StringComparison.Ordinal) ||
            !string.Equals(request.Draft.OrganizationId, reservation.OrganizationId, StringComparison.Ordinal)) return Results.NotFound();
        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null) return Results.NotFound();
        try
        {
            var draft = (CreateSessionDraftCommand)CommunitySessionEndpoints.MapCreateRequest(request.Draft);
            var result = await service.ExecuteAsync(draft with { SessionId = reservation.SessionId }, actor.Value!, ct);
            return result.SessionId == reservation.SessionId && result.Version > 0
                ? Results.Ok(result) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return CommunitySessionEndpoints.MapCommandException(error); }
    }

    private static async Task<bool> VerifyAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await CaseCommandEndpoints.RequiresAntiforgeryAsync(context)) return true;
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
