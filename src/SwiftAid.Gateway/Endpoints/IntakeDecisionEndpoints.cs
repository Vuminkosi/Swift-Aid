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

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Maps the authenticated staff endpoint for reviewer decisions on intake reports.</summary>
public static class IntakeDecisionEndpoints
{
    private const int SupportedWireSchemaVersion = 1;
    private const int MaximumRequestBodyBytes = 4096;

    public static void MapIntakeDecisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/v1/intake/{id}/decisions", DecideAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = string.Join(
                    ",",
                    IdentityConstants.ApplicationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("RecordIntakeDecision")
            .Accepts<IntakeDecisionApiRequest>("application/json")
            .Produces<IntakeReceipt>(StatusCodes.Status200OK)
            .Produces<IntakePromotionApiResponse>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Converts the fixed version-one wire request to the trusted application decision.</summary>
    public static IntakeDecision MapRequest(Guid receiptId, IntakeDecisionApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (receiptId == Guid.Empty)
        {
            throw new ArgumentException("A receipt identifier is required.", nameof(receiptId));
        }

        if (request.SchemaVersion != SupportedWireSchemaVersion)
        {
            throw new ArgumentException("The intake decision schema version is not supported.", nameof(request));
        }

        if (request.ExpectedVersion < 1)
        {
            throw new ArgumentException("A positive expected report version is required.", nameof(request));
        }

        if (!Enum.TryParse<IntakeDecisionKind>(request.Decision, ignoreCase: false, out var decision) ||
            !Enum.IsDefined(decision))
        {
            throw new ArgumentException("The intake decision kind is not supported.", nameof(request));
        }

        if (request.ReviewReason is { Length: > 1000 })
        {
            throw new ArgumentException("The review reason is too long.", nameof(request));
        }

        if (decision == IntakeDecisionKind.LinkAsEvidence)
        {
            if (request.TargetCaseId is not Guid wireTarget || wireTarget == Guid.Empty)
            {
                throw new ArgumentException("Linking evidence requires a target case.", nameof(request));
            }
        }
        else if (request.TargetCaseId is not null)
        {
            throw new ArgumentException("Only evidence linkage accepts a target case.", nameof(request));
        }

        return new IntakeDecision(receiptId, request.ExpectedVersion, decision, request.ReviewReason, request.TargetCaseId);
    }

    /// <summary>
    /// Maps service outcomes to HTTP. Missing and unauthorized receipts share one generic 404 so
    /// the response never reveals whether a receipt exists or who may review it. A promotion
    /// that created a case returns a staff-scoped response carrying the new case identifier.
    /// </summary>
    public static IResult MapResult(IntakeDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status == IntakeDecisionStatus.Applied && result.Receipt is not null &&
            result.PromotedCaseId is { } promotedCaseId && promotedCaseId != Guid.Empty)
        {
            return Results.Json(
                new IntakePromotionApiResponse(
                    result.Receipt.ReceiptId,
                    result.Receipt.ClientSubmissionId,
                    result.Receipt.Status,
                    result.Receipt.RoutingStatus,
                    result.Receipt.ReceivedAtUtc,
                    promotedCaseId),
                options: JsonOptions,
                statusCode: StatusCodes.Status200OK);
        }

        return result.Status switch
        {
            IntakeDecisionStatus.Applied when result.Receipt is not null =>
                Results.Json(result.Receipt, options: JsonOptions, statusCode: StatusCodes.Status200OK),
            IntakeDecisionStatus.NotFoundOrNotAuthorized =>
                Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The intake decision was not found."),
            IntakeDecisionStatus.Conflict =>
                Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The report changed before the decision was recorded."),
            IntakeDecisionStatus.Invalid =>
                Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The intake decision is invalid."),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The intake decision is temporarily unavailable.")
        };
    }

    private static async Task<IResult> DecideAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IIntakeReviewAuthorizer authorizer,
        IIntakeService intake,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        ActorContext actor;
        try
        {
            actor = ActorContext.FromValidatedPrincipal(context.User);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (!context.Request.RouteValues.TryGetValue("id", out var routeId) ||
            routeId is not string routeText ||
            !Guid.TryParseExact(routeText, "D", out var receiptId) || receiptId == Guid.Empty)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The intake decision is invalid.");
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
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The intake decision is invalid.");
        }

        byte[] requestBody;
        try
        {
            requestBody = await ReadBoundedRequestBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The intake decision request is too large.");
        }

        IntakeDecisionApiRequest? request;
        IntakeDecision decision;
        try
        {
            request = JsonSerializer.Deserialize<IntakeDecisionApiRequest>(requestBody, JsonOptions);
            if (request is null)
            {
                throw new ArgumentException("An intake decision request is required.", nameof(request));
            }

            decision = MapRequest(receiptId, request);
        }
        catch (Exception exception) when (IsRequestValidationException(exception))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The intake decision is invalid.");
        }

        try
        {
            var authorized = decision.Decision == IntakeDecisionKind.LinkAsEvidence && decision.TargetCaseId is { } linkTarget
                ? await authorizer.IsLinkAuthorizedAsync(actor, decision.ReceiptId, linkTarget, cancellationToken).ConfigureAwait(false)
                : await authorizer.IsAuthorizedAsync(actor, decision.ReceiptId, cancellationToken).ConfigureAwait(false);

            if (!authorized)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The intake decision was not found.");
            }

            return MapResult(await intake.DecideAsync(decision, actor, cancellationToken).ConfigureAwait(false));
        }
        catch (IntakePromotionUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Case promotion is temporarily unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The intake decision is temporarily unavailable.");
        }
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
        // Authenticate against the configured server schemes instead of trusting request headers
        // or caller-provided fields to distinguish browser and native clients. If a valid browser
        // cookie is also present, keep the CSRF check even if another scheme already authenticated the call.
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

/// <summary>Wire-only reviewer decision request; receipt identity travels in the route.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntakeDecisionApiRequest(
    int SchemaVersion,
    long ExpectedVersion,
    string? Decision,
    string? ReviewReason,
    Guid? TargetCaseId = null);

/// <summary>Staff-scoped promotion receipt; carries the newly registered case identifier.</summary>
public sealed record IntakePromotionApiResponse(
    Guid ReceiptId,
    Guid ClientSubmissionId,
    IntakeStatus Status,
    IntakeRoutingStatus RoutingStatus,
    DateTimeOffset ReceivedAtUtc,
    Guid CaseId);
