using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Authenticated, own-account event subscription operations. No roster endpoint exists.</summary>
public static class EventSubscriptionEndpoints
{
    private const int MaximumBodyBytes = 8 * 1024;

    public static void MapEventSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routes = endpoints.MapGroup("/api/v1/sessions/{sessionId:guid}/subscription")
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" });
        routes.AddEndpointFilter(NoStoreAsync);

        routes.MapGet("/", GetAsync)
            .WithName("GetOwnEventSubscription")
            .Produces<EventSubscription>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        routes.MapGet("/options", GetOptionsAsync)
            .WithName("GetEventSubscriptionOptions")
            .Produces<EventSubscriptionOptions>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        routes.MapPost("/", SubscribeAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumBodyBytes))
            .WithName("SubscribeToPublicSession")
            .Accepts<SubscribeEventRequest>("application/json")
            .Produces<EventSubscription>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        routes.MapPost("/unsubscribe", UnsubscribeAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumBodyBytes))
            .WithName("UnsubscribeFromPublicSession")
            .Accepts<UnsubscribeEventRequest>("application/json")
            .Produces<EventSubscription>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        endpoints.MapGet("/api/v1/my/session-subscriptions", ListMineAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" })
            .AddEndpointFilter(NoStoreAsync)
            .WithName("ListOwnEventSubscriptions")
            .Produces<IReadOnlyList<EventSubscription>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized);
    }

    private static ValueTask<object?> NoStoreAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return next(context);
    }

    private static async Task<IResult> ListMineAsync(
        HttpContext context,
        [FromServices] IEventSubscriptionService service,
        CancellationToken cancellationToken)
    {
        var accountId = AccountId(context.User);
        if (accountId is null) return Results.Unauthorized();
        return Results.Ok(await service.ListMineAsync(accountId, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        Guid sessionId,
        [FromServices] IEventSubscriptionService service,
        CancellationToken cancellationToken)
    {
        var accountId = AccountId(context.User);
        if (accountId is null) return Results.Unauthorized();
        var subscription = await service.GetAsync(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        return subscription is null ? PublicSessionEndpoints.MapMissing() : Results.Ok(subscription);
    }

    private static async Task<IResult> GetOptionsAsync(
        HttpContext context,
        Guid sessionId,
        [FromServices] IEventSubscriptionService service,
        CancellationToken cancellationToken)
    {
        var accountId = AccountId(context.User);
        if (accountId is null) return Results.Unauthorized();
        var options = await service.GetOptionsAsync(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        return options is null ? PublicSessionEndpoints.MapMissing() : Results.Ok(options);
    }

    private static async Task<IResult> SubscribeAsync(
        HttpContext context,
        Guid sessionId,
        SubscribeEventRequest? request,
        [FromServices] IEventSubscriptionService service,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!await HasValidRequestAsync(context, antiforgery).ConfigureAwait(false))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The request could not be validated.");
        var accountId = AccountId(context.User);
        if (accountId is null) return Results.Unauthorized();
        if (request is null || request.SessionVersion < 1 || string.IsNullOrWhiteSpace(request.ConsentReference) ||
            string.IsNullOrWhiteSpace(request.Channel) || request.ConsentReference.Length > 160 || request.Channel.Length > 32)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The reminder choice is invalid.");

        try
        {
            var subscription = await service.SubscribeAsync(sessionId, accountId, request.SessionVersion,
                request.ConsentReference, request.Channel, cancellationToken).ConfigureAwait(false);
            return Results.Ok(subscription);
        }
        catch (SessionSubscriptionConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The session or subscription changed.");
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The reminder choice is invalid.");
        }
        catch (KeyNotFoundException)
        {
            return PublicSessionEndpoints.MapMissing();
        }
    }

    private static async Task<IResult> UnsubscribeAsync(
        HttpContext context,
        Guid sessionId,
        UnsubscribeEventRequest? request,
        [FromServices] IEventSubscriptionService service,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!await HasValidRequestAsync(context, antiforgery).ConfigureAwait(false))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The request could not be validated.");
        var accountId = AccountId(context.User);
        if (accountId is null) return Results.Unauthorized();
        if (request is null || request.ExpectedVersion < 0)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The subscription version is invalid.");

        try
        {
            return Results.Ok(await service.UnsubscribeAsync(sessionId, accountId, request.ExpectedVersion, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (SessionSubscriptionConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The subscription changed.");
        }
    }

    private static async Task<bool> HasValidRequestAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await CaseCommandEndpoints.RequiresAntiforgeryAsync(context).ConfigureAwait(false))
            return true;
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static string? AccountId(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(value) || value.Length > 200 || value != value.Trim() || value.Any(char.IsControl)
            ? null
            : value;
    }

    public sealed record SubscribeEventRequest(long SessionVersion, string ConsentReference, string Channel);
    public sealed record UnsubscribeEventRequest(long ExpectedVersion);
}
