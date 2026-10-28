using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Maps the authenticated staff endpoint for managed-case non-creation commands.</summary>
/// <remarks>
/// Denial and absence share one generic 404 so the response never reveals whether a case
/// exists or who may command it. The wire request carries command routing fields only;
/// authorization always derives from the server-authenticated principal.
/// </remarks>
public static class CaseCommandEndpoints
{
    private const int SupportedWireSchemaVersion = 1;
    private const int MaximumRequestBodyBytes = 64 * 1024;

    /// <summary>
    /// Adds the versioned case-command route. The caller must register an
    /// <see cref="ICaseCommandProcessor"/> before this route is mapped.
    /// </summary>
    public static void MapCaseCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schemes = string.Join(
            ",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        endpoints.MapPost("/api/v1/cases/{caseId:guid}/commands", PostCommandAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("PostCaseCommand")
            .Accepts<CaseCommandApiRequest>("application/json")
            .Produces<CaseCommandApiResponse>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Converts the fixed version-one wire request to the trusted application command.</summary>
    public static CaseCommand MapRequest(Guid caseId, CaseCommandApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("A case identifier is required.", nameof(caseId));
        }

        if (request.SchemaVersion != SupportedWireSchemaVersion)
        {
            throw new ArgumentException("The case command schema version is not supported.", nameof(request));
        }

        if (request.CommandId == Guid.Empty)
        {
            throw new ArgumentException("A stable command identifier is required.", nameof(request));
        }

        if (request.ExpectedVersion < 2)
        {
            throw new ArgumentException("A non-creation case command requires an existing aggregate version.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CommandType))
        {
            throw new ArgumentException("A case command type is required.", nameof(request));
        }

        // The processor replaces this placeholder with the database clock; it is never trusted.
        var recordedAt = DateTimeOffset.UtcNow;

        return request.CommandType switch
        {
            "CorrectReport" => new CorrectReportCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireReason(request),
                RequireReason(request),
                request.OccurredAtUtc,
                recordedAt),
            "RequestProtectedAction" => new RequestCaseActionCommand(
                request.CommandId,
                request.ExpectedVersion,
                request.RequestId ?? request.CommandId,
                RequireAction(request),
                RequireReason(request),
                [],
                RequireActionOwner(request),
                request.OccurredAtUtc,
                recordedAt),
            "ReviewProtectedAction" => new ReviewCaseActionRequestCommand(
                request.CommandId,
                request.ExpectedVersion,
                request.RequestId ?? request.CommandId,
                RequireOutcome(request),
                RequireOptionalOwnerId(request.ProposedOwnerId),
                RequireReason(request),
                request.OccurredAtUtc,
                recordedAt),
            "Resolve" => new ResolveCaseCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireReason(request),
                request.OccurredAtUtc,
                recordedAt),
            "Reopen" => new ReopenCaseCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireReason(request),
                RequireOwnerId(request),
                request.OccurredAtUtc,
                recordedAt),
            "RecordObservation" => new RecordObservationCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireObservationId(request),
                RequireNarrative(request),
                RequireSource(request),
                RequireReason(request),
                request.OccurredAtUtc,
                recordedAt),
            "ScheduleFollowUp" => new ScheduleFollowUpCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireFollowUpId(request),
                RequirePurpose(request),
                RequireAssigneeActorId(request),
                RequireDueAtUtc(request),
                RequireReason(request),
                request.OccurredAtUtc,
                recordedAt),
            "RecordFollowUpOutcome" => new RecordFollowUpOutcomeCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireFollowUpId(request),
                RequireFollowUpOutcome(request),
                RequireOutcomeNotes(request),
                request.NewDueAtUtc,
                RequireReason(request),
                request.OccurredAtUtc,
                recordedAt),
            "RaiseEscalation" => new RaiseEscalationCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireEscalationId(request),
                RequireReason(request),
                RequireEvidenceEventIds(request),
                RequireRecipientRoute(request),
                request.OccurredAtUtc,
                recordedAt),
            "AcknowledgeEscalation" => new AcknowledgeEscalationCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireEscalationId(request),
                RequireOptionalActionSummary(request.ActionSummary),
                RequireReason(request),
                request.OccurredAtUtc,
                recordedAt),
            "CloseEscalation" => new CloseEscalationCommand(
                request.CommandId,
                request.ExpectedVersion,
                RequireEscalationId(request),
                RequireResolution(request),
                RequireReason(request),
                request.OccurredAtUtc,
                recordedAt),
            _ => throw new ArgumentException("The case command type is not supported.", nameof(request)),
        };
    }

    /// <summary>Maps processor outcomes to HTTP; denial and absence share one generic 404.</summary>
    public static IResult MapResult(Guid caseId, Guid commandId, CaseCommandProcessingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.CaseId != caseId || result.CommandId != commandId)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The case command is temporarily unavailable.");
        }

        return result.Status switch
        {
            CaseCommandProcessingStatus.Accepted when HasAcceptedIdentifiers(result) =>
                Results.Json(ToResponse(result), options: JsonOptions, statusCode: StatusCodes.Status200OK),
            CaseCommandProcessingStatus.Replayed when HasAcceptedIdentifiers(result) =>
                Results.Json(ToResponse(result), options: JsonOptions, statusCode: StatusCodes.Status200OK),
            CaseCommandProcessingStatus.Denied =>
                Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The case was not found."),
            CaseCommandProcessingStatus.Conflict =>
                Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The case changed before the command was recorded."),
            CaseCommandProcessingStatus.Invalid =>
                Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The case command is invalid."),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The case command is temporarily unavailable.")
        };
    }

    private static async Task<IResult> PostCommandAsync(
        HttpContext context,
        Guid caseId,
        IAntiforgery antiforgery,
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

        if (caseId == Guid.Empty)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The case identifier is invalid.");
        }

        // The command pipeline may be disabled in a deployment; fail closed with the same generic 404.
        var processor = context.RequestServices.GetService<ICaseCommandProcessor>();
        if (processor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The case was not found.");
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
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The case command is invalid.");
        }

        byte[] requestBody;
        try
        {
            requestBody = await ReadBoundedRequestBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The case command request is too large.");
        }

        CaseCommandApiRequest? request;
        CaseCommand command;
        try
        {
            request = JsonSerializer.Deserialize<CaseCommandApiRequest>(requestBody, JsonOptions);
            if (request is null)
            {
                throw new ArgumentException("A case command request is required.", nameof(request));
            }

            command = MapRequest(caseId, request);
        }
        catch (Exception exception) when (IsRequestValidationException(exception))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The case command is invalid.");
        }

        try
        {
            var result = await processor.ProcessAsync(
                actor, caseId, request.SchemaVersion, command, cancellationToken).ConfigureAwait(false);
            return MapResult(caseId, request.CommandId, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The case command is temporarily unavailable.");
        }
    }

    private static string RequireReason(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("A case command reason is required.", nameof(request));
        }

        return request.Reason;
    }

    private static string RequireOwnerId(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProposedOwnerId) ||
            !string.Equals(request.ProposedOwnerId, request.ProposedOwnerId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical accountable owner identifier is required.", nameof(request));
        }

        return request.ProposedOwnerId;
    }

    private static string? RequireOptionalOwnerId(string? proposedOwnerId)
    {
        if (proposedOwnerId is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(proposedOwnerId) ||
            !string.Equals(proposedOwnerId, proposedOwnerId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The proposed owner identifier is invalid.", nameof(proposedOwnerId));
        }

        return proposedOwnerId;
    }

    private static CaseAction RequireAction(CaseCommandApiRequest request)
    {
        if (!Enum.TryParse<CaseAction>(request.Action, ignoreCase: false, out var action) ||
            !Enum.IsDefined(action))
        {
            throw new ArgumentException("The protected action is not supported.", nameof(request));
        }

        return action;
    }

    private static string? RequireActionOwner(CaseCommandApiRequest request)
    {
        var action = RequireAction(request);
        if (action is CaseAction.Reassign or CaseAction.Assist)
        {
            if (string.IsNullOrWhiteSpace(request.ProposedOwnerId) ||
                !string.Equals(request.ProposedOwnerId, request.ProposedOwnerId.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Reassignment requires a canonical accountable owner identifier.", nameof(request));
            }

            return request.ProposedOwnerId;
        }

        return RequireOptionalOwnerId(request.ProposedOwnerId);
    }

    private static CaseActionReviewOutcome RequireOutcome(CaseCommandApiRequest request)
    {
        if (!Enum.TryParse<CaseActionReviewOutcome>(request.Outcome, ignoreCase: false, out var outcome) ||
            !Enum.IsDefined(outcome))
        {
            throw new ArgumentException("The protected-action review outcome is not supported.", nameof(request));
        }

        return outcome;
    }

    private static Guid RequireObservationId(CaseCommandApiRequest request)
    {
        if (request.ObservationId is null || request.ObservationId == Guid.Empty)
        {
            throw new ArgumentException("An observation identifier is required.", nameof(request));
        }

        return request.ObservationId.Value;
    }

    private static Guid RequireFollowUpId(CaseCommandApiRequest request)
    {
        if (request.FollowUpId is null || request.FollowUpId == Guid.Empty)
        {
            throw new ArgumentException("A follow-up identifier is required.", nameof(request));
        }

        return request.FollowUpId.Value;
    }

    private static Guid RequireEscalationId(CaseCommandApiRequest request)
    {
        if (request.EscalationId is null || request.EscalationId == Guid.Empty)
        {
            throw new ArgumentException("An escalation identifier is required.", nameof(request));
        }

        return request.EscalationId.Value;
    }

    private static string RequireNarrative(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Narrative))
        {
            throw new ArgumentException("An observation narrative is required.", nameof(request));
        }

        return request.Narrative;
    }

    private static string RequireSource(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
        {
            throw new ArgumentException("An observation source is required.", nameof(request));
        }

        return request.Source;
    }

    private static string RequirePurpose(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Purpose))
        {
            throw new ArgumentException("A follow-up purpose is required.", nameof(request));
        }

        return request.Purpose;
    }

    private static string RequireAssigneeActorId(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AssigneeActorId))
        {
            throw new ArgumentException("A follow-up assignee is required.", nameof(request));
        }

        return request.AssigneeActorId;
    }

    private static DateTimeOffset RequireDueAtUtc(CaseCommandApiRequest request)
    {
        if (request.DueAtUtc is null)
        {
            throw new ArgumentException("A follow-up due time is required.", nameof(request));
        }

        return request.DueAtUtc.Value;
    }

    private static FollowUpOutcome RequireFollowUpOutcome(CaseCommandApiRequest request)
    {
        if (!Enum.TryParse<FollowUpOutcome>(request.OutcomeName, ignoreCase: false, out var outcome) ||
            !Enum.IsDefined(outcome))
        {
            throw new ArgumentException("The follow-up outcome is not supported.", nameof(request));
        }

        return outcome;
    }

    private static string RequireOutcomeNotes(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutcomeNotes))
        {
            throw new ArgumentException("Follow-up outcome notes are required.", nameof(request));
        }

        return request.OutcomeNotes;
    }

    private static string RequireRecipientRoute(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RecipientRoute))
        {
            throw new ArgumentException("An escalation recipient route is required.", nameof(request));
        }

        return request.RecipientRoute;
    }

    private static string RequireResolution(CaseCommandApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Resolution))
        {
            throw new ArgumentException("An escalation resolution is required.", nameof(request));
        }

        return request.Resolution;
    }

    private static string? RequireOptionalActionSummary(string? actionSummary)
    {
        if (actionSummary is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(actionSummary))
        {
            throw new ArgumentException("The escalation action summary is invalid.", nameof(actionSummary));
        }

        return actionSummary;
    }

    private static IReadOnlyList<string> RequireEvidenceEventIds(CaseCommandApiRequest request)
    {
        if (request.EvidenceEventIds is null)
        {
            return [];
        }

        foreach (var evidenceEventId in request.EvidenceEventIds)
        {
            if (string.IsNullOrWhiteSpace(evidenceEventId))
            {
                throw new ArgumentException("The escalation evidence references are invalid.", nameof(request));
            }
        }

        return request.EvidenceEventIds;
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
        // Authenticate against the configured server schemes instead of trusting request headers
        // or caller-provided fields to distinguish browser and native clients. If a valid browser
        // cookie is also present, keep the CSRF check even when a bearer token is supplied.
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

    private static bool HasAcceptedIdentifiers(CaseCommandProcessingResult result) =>
        result.CaseId != Guid.Empty &&
        result.CommandId != Guid.Empty &&
        !result.EventIds.IsDefault && result.EventIds.Length > 0 && result.EventIds.All(eventId => eventId != Guid.Empty) &&
        result.AggregateVersion is > 0 && result.ReportRevision is > 0;

    private static CaseCommandApiResponse ToResponse(CaseCommandProcessingResult result) =>
        new(result.CaseId, result.CommandId, result.EventIds.ToArray(), result.AggregateVersion!.Value, result.ReportRevision!.Value);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class RequestBodyTooLargeException : Exception;
}

/// <summary>Version-one wire request for a managed-case non-creation command.</summary>
/// <param name="SchemaVersion">Wire schema version; only 1 is supported.</param>
/// <param name="CommandType">One of CorrectReport, RequestProtectedAction, ReviewProtectedAction, Resolve, Reopen, RecordObservation, ScheduleFollowUp, RecordFollowUpOutcome, RaiseEscalation, AcknowledgeEscalation, CloseEscalation.</param>
/// <param name="CommandId">Stable idempotency key for the command.</param>
/// <param name="ExpectedVersion">Aggregate version the command applies to; at least 2.</param>
/// <param name="OccurredAtUtc">Optional source occurrence time; the server clock is authoritative for recording.</param>
/// <param name="Reason">Operator reason; required by every command type.</param>
/// <param name="ProposedOwnerId">Reassignment target or reopened-case owner; required by Reopen.</param>
/// <param name="RequestId">Protected-action request key; defaults to the command id when absent.</param>
/// <param name="Outcome">Review outcome (Pending, Denied, Approved); required by ReviewProtectedAction.</param>
/// <param name="ObservationId">Observation key; required by RecordObservation.</param>
/// <param name="Narrative">Observation narrative; required by RecordObservation.</param>
/// <param name="Source">Observation source; required by RecordObservation.</param>
/// <param name="FollowUpId">Follow-up key; required by ScheduleFollowUp and RecordFollowUpOutcome.</param>
/// <param name="Purpose">Follow-up purpose; required by ScheduleFollowUp.</param>
/// <param name="AssigneeActorId">Follow-up assignee; required by ScheduleFollowUp.</param>
/// <param name="DueAtUtc">Follow-up due time; required by ScheduleFollowUp.</param>
/// <param name="OutcomeName">Follow-up outcome name (Completed, Missed, Unreachable, Rescheduled, Cancelled); required by RecordFollowUpOutcome.</param>
/// <param name="OutcomeNotes">Follow-up outcome notes; required by RecordFollowUpOutcome.</param>
/// <param name="NewDueAtUtc">Replacement due time; used by RecordFollowUpOutcome only when rescheduling.</param>
/// <param name="EscalationId">Escalation key; required by RaiseEscalation, AcknowledgeEscalation, CloseEscalation.</param>
/// <param name="RecipientRoute">Configured escalation recipient route; required by RaiseEscalation. Never a default.</param>
/// <param name="Resolution">Escalation resolution; required by CloseEscalation.</param>
/// <param name="ActionSummary">Optional action summary recorded with an acknowledgement.</param>
/// <param name="EvidenceEventIds">Escalation evidence references for RaiseEscalation.</param>
public sealed record CaseCommandApiRequest(
    int SchemaVersion,
    string? CommandType,
    Guid CommandId,
    long ExpectedVersion,
    DateTimeOffset? OccurredAtUtc,
    string? Reason,
    string? ProposedOwnerId,
    Guid? RequestId,
    string? Outcome,
    string? Action,
    Guid? ObservationId = null,
    string? Narrative = null,
    string? Source = null,
    Guid? FollowUpId = null,
    string? Purpose = null,
    string? AssigneeActorId = null,
    DateTimeOffset? DueAtUtc = null,
    string? OutcomeName = null,
    string? OutcomeNotes = null,
    DateTimeOffset? NewDueAtUtc = null,
    Guid? EscalationId = null,
    string? RecipientRoute = null,
    string? Resolution = null,
    string? ActionSummary = null,
    IReadOnlyList<string>? EvidenceEventIds = null);

/// <summary>Accepted or replayed case-command receipt.</summary>
public sealed record CaseCommandApiResponse(
    Guid CaseId,
    Guid CommandId,
    IReadOnlyList<Guid> EventIds,
    long AggregateVersion,
    long ReportRevision);
