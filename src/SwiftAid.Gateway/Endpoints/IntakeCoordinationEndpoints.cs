using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Infrastructure.Intake;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Maps the authenticated staff endpoints for the PC-09 intake coordination queue.</summary>
/// <remarks>
/// Only platform coordinators reach these endpoints; missing and unauthorized receipts
/// share one generic 404 so the response never reveals whether a receipt exists or who
/// may coordinate it. Coordinator projections carry routing fields only: narrative,
/// observations, precise coordinates, and original audio are never returned here.
/// </remarks>
public static class IntakeCoordinationEndpoints
{
    private const int SupportedWireSchemaVersion = 1;
    private const int MaximumRequestBodyBytes = 4096;

    public static void MapIntakeCoordinationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/v1/intake/coordination/queue", GetQueueAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" })
            .WithName("GetIntakeCoordinationQueue")
            .Produces<IReadOnlyList<IntakeCoordinationQueueEntry>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/v1/intake/coordination/{id}", GetDetailAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" })
            .WithName("GetIntakeCoordinationDetail")
            .Produces<IntakeCoordinationDetail>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/v1/intake/coordination/queues", GetQueuesAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" })
            .WithName("GetIntakeCoordinationQueues")
            .Produces<IReadOnlyList<IntakeCoordinationQueueOption>>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        endpoints.MapPost("/api/v1/intake/coordination/{id}/actions", PostActionAsync)
            .RequireAuthorization(new AuthorizeAttribute { Policy = "SwiftAidStaffApi" })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("RecordIntakeCoordinationAction")
            .Accepts<IntakeCoordinationActionRequest>("application/json")
            .Produces<IntakeReceipt>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Converts the fixed version-one wire request to the trusted application command.</summary>
    public static IntakeCoordinationCommand MapRequest(Guid receiptId, IntakeCoordinationActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (receiptId == Guid.Empty)
        {
            throw new ArgumentException("A receipt identifier is required.", nameof(receiptId));
        }

        if (request.SchemaVersion != SupportedWireSchemaVersion)
        {
            throw new ArgumentException("The intake coordination schema version is not supported.", nameof(request));
        }

        if (request.ExpectedVersion < 1)
        {
            throw new ArgumentException("A positive expected report version is required.", nameof(request));
        }

        if (!Enum.TryParse<IntakeCoordinationAction>(request.Action, ignoreCase: false, out var action) ||
            !Enum.IsDefined(action))
        {
            throw new ArgumentException("The intake coordination action is not supported.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
        {
            throw new ArgumentException("A bounded coordination reason is required.", nameof(request));
        }

        if (action == IntakeCoordinationAction.AssignQueue)
        {
            if (string.IsNullOrWhiteSpace(request.TargetQueueId) || request.TargetQueueId.Length > 128)
            {
                throw new ArgumentException("Assigning a queue requires an eligible queue identifier.", nameof(request));
            }
        }
        else if (request.TargetQueueId is not null)
        {
            throw new ArgumentException("Only queue assignment accepts a target queue.", nameof(request));
        }

        return new IntakeCoordinationCommand(receiptId, request.ExpectedVersion, action, request.TargetQueueId, request.Reason);
    }

    /// <summary>Maps service outcomes to HTTP; missing and unauthorized share one generic 404.</summary>
    public static IResult MapActionResult(IntakeDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            IntakeDecisionStatus.Applied when result.Receipt is not null =>
                Results.Json(result.Receipt, options: JsonOptions, statusCode: StatusCodes.Status200OK),
            IntakeDecisionStatus.NotFoundOrNotAuthorized =>
                Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The coordination record was not found."),
            IntakeDecisionStatus.Conflict =>
                Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The report changed before the coordination action was recorded."),
            IntakeDecisionStatus.Invalid =>
                Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The coordination action is invalid."),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The coordination action is temporarily unavailable.")
        };
    }

    private static async Task<IResult> GetQueueAsync(
        HttpContext context,
        IIntakeCoordinationAuthorizer authorizer,
        IIntakeCoordinationService coordination,
        IntakeCoordinationOptions options,
        CancellationToken cancellationToken)
    {
        ActorContext? actor;
        try
        {
            actor = TryGetActor(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (actor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (await authorizer.AuthorizeAsync(actor, cancellationToken).ConfigureAwait(false) is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The coordination record was not found.");
        }

        IntakeRoutingStatus? routing = null;
        if (context.Request.Query.TryGetValue("routingStatus", out var routingText) &&
            routingText.Count > 0 && !string.IsNullOrWhiteSpace(routingText[0]))
        {
            if (!Enum.TryParse<IntakeRoutingStatus>(routingText[0], ignoreCase: false, out var parsed) ||
                !Enum.IsDefined(parsed) ||
                parsed is not (IntakeRoutingStatus.NeedsRouting or IntakeRoutingStatus.NoServiceCoverage))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The routing filter is invalid.");
            }

            routing = parsed;
        }

        var limit = options.DefaultPageLimit;
        if (context.Request.Query.TryGetValue("limit", out var limitText) && limitText.Count > 0 &&
            (!int.TryParse(limitText[0], out limit) || limit < 1))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The page limit is invalid.");
        }

        limit = Math.Min(limit, options.MaximumPageLimit);

        DateTimeOffset? receivedBefore = null;
        if (context.Request.Query.TryGetValue("receivedBeforeUtc", out var beforeText) && beforeText.Count > 0 &&
            !string.IsNullOrWhiteSpace(beforeText[0]))
        {
            if (!DateTimeOffset.TryParse(beforeText[0], out var parsedBefore))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The page cursor is invalid.");
            }

            receivedBefore = parsedBefore;
        }

        Guid? receiptBefore = null;
        if (context.Request.Query.TryGetValue("receiptBefore", out var receiptText) && receiptText.Count > 0 &&
            !string.IsNullOrWhiteSpace(receiptText[0]))
        {
            if (!Guid.TryParseExact(receiptText[0], "D", out var parsedReceipt) || parsedReceipt == Guid.Empty)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The page cursor is invalid.");
            }

            receiptBefore = parsedReceipt;
        }

        if ((receivedBefore is null) != (receiptBefore is null))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The page cursor is incomplete.");
        }

        try
        {
            var entries = await coordination.GetQueueAsync(
                actor, routing, limit, receivedBefore, receiptBefore, cancellationToken).ConfigureAwait(false);
            return Results.Json(entries, options: JsonOptions, statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The coordination queue is temporarily unavailable.");
        }
    }

    private static async Task<IResult> GetDetailAsync(
        HttpContext context,
        IIntakeCoordinationService coordination,
        CancellationToken cancellationToken)
    {
        ActorContext? actor;
        try
        {
            actor = TryGetActor(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (actor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (!context.Request.RouteValues.TryGetValue("id", out var routeId) ||
            routeId is not string routeText ||
            !Guid.TryParseExact(routeText, "D", out var receiptId) || receiptId == Guid.Empty)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The coordination record is invalid.");
        }

        try
        {
            var detail = await coordination.GetDetailAsync(actor, receiptId, cancellationToken).ConfigureAwait(false);
            return detail is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The coordination record was not found.")
                : Results.Json(detail, options: JsonOptions, statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The coordination record is temporarily unavailable.");
        }
    }

    private static async Task<IResult> GetQueuesAsync(
        HttpContext context,
        IIntakeCoordinationAuthorizer authorizer,
        IIntakeCoordinationService coordination,
        CancellationToken cancellationToken)
    {
        ActorContext? actor;
        try
        {
            actor = TryGetActor(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (actor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (await authorizer.AuthorizeAsync(actor, cancellationToken).ConfigureAwait(false) is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The coordination record was not found.");
        }

        try
        {
            var queues = await coordination.GetEligibleQueuesAsync(actor, cancellationToken).ConfigureAwait(false);
            return Results.Json(queues, options: JsonOptions, statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The coordination queues are temporarily unavailable.");
        }
    }

    private static async Task<IResult> PostActionAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IIntakeCoordinationService coordination,
        CancellationToken cancellationToken)
    {
        ActorContext? actor;
        try
        {
            actor = TryGetActor(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (actor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (!context.Request.RouteValues.TryGetValue("id", out var routeId) ||
            routeId is not string routeText ||
            !Guid.TryParseExact(routeText, "D", out var receiptId) || receiptId == Guid.Empty)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The coordination action is invalid.");
        }

        if (await RequiresAntiforgeryAsync(context).ConfigureAwait(false))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Request verification failed.");
            }
        }

        if (!context.Request.HasJsonContentType())
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The coordination action is invalid.");
        }

        byte[] requestBody;
        try
        {
            requestBody = await ReadBoundedRequestBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The coordination request is too large.");
        }

        IntakeCoordinationActionRequest? request;
        IntakeCoordinationCommand command;
        try
        {
            request = JsonSerializer.Deserialize<IntakeCoordinationActionRequest>(requestBody, JsonOptions);
            if (request is null)
            {
                throw new ArgumentException("A coordination request is required.", nameof(request));
            }

            command = MapRequest(receiptId, request);
        }
        catch (Exception exception) when (IsRequestValidationException(exception))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The coordination action is invalid.");
        }

        try
        {
            return MapActionResult(await coordination.ActAsync(command, actor, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The coordination action is temporarily unavailable.");
        }
    }

    private static ActorContext? TryGetActor(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return ActorContext.FromValidatedPrincipal(context.User);
    }

    private static async Task<byte[]> ReadBoundedRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumRequestBodyBytes + 1];
        var totalBytesRead = 0;

        while (totalBytesRead < buffer.Length)
        {
            var bytesRead = await request.Body.ReadAsync(
                buffer.AsMemory(totalBytesRead, buffer.Length - totalBytesRead),
                cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return buffer.AsSpan(0, totalBytesRead).ToArray();
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > MaximumRequestBodyBytes)
            {
                throw new RequestBodyTooLargeException();
            }
        }

        throw new RequestBodyTooLargeException();
    }

    private static async Task<bool> RequiresAntiforgeryAsync(HttpContext context)
    {
        var cookieAuthentication = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme)
            .ConfigureAwait(false);
        if (cookieAuthentication.Succeeded)
        {
            return true;
        }

        var bearerAuthentication = await context.AuthenticateAsync(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
        return !bearerAuthentication.Succeeded;
    }

    private static bool IsRequestValidationException(Exception exception) =>
        exception is ArgumentException or InvalidDataException or JsonException or NotSupportedException or InvalidOperationException;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class RequestBodyTooLargeException : Exception
    {
    }
}
