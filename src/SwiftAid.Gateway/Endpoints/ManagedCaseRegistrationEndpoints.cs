using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Maps the authenticated staff endpoint for managed human and animal case registration.</summary>
public static class ManagedCaseRegistrationEndpoints
{
    private const int SupportedWireSchemaVersion = 1;
    private const int MaximumRequestBodyBytes = 64 * 1024;

    /// <summary>
    /// Adds the versioned managed-case registration route. The caller must register the application
    /// processor before this route is mapped.
    /// </summary>
    public static void MapManagedCaseRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/v1/cases", RegisterAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = string.Join(
                    ",",
                    IdentityConstants.ApplicationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            })
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("RegisterManagedCase")
            .Accepts<ManagedCaseRegistrationApiRequest>("application/json")
            .Produces<ManagedCaseRegistrationResponse>(StatusCodes.Status201Created)
            .Produces<ManagedCaseRegistrationResponse>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Converts the fixed version-one wire request to the trusted application request.</summary>
    public static RegisterManagedCaseRequest MapRequest(ManagedCaseRegistrationApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != SupportedWireSchemaVersion)
        {
            throw new ArgumentException("The managed-case request schema version is not supported.", nameof(request));
        }

        if (request.CommandId == Guid.Empty)
        {
            throw new ArgumentException("A stable command identifier is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.WorkspaceId) ||
            !string.Equals(request.WorkspaceId, request.WorkspaceId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical workspace identifier is required.", nameof(request));
        }

        if (request.Report is null)
        {
            throw new ArgumentException("A typed report is required.", nameof(request));
        }

        var report = ToDomainReport(request.Report);
        return new RegisterManagedCaseRequest(request.CommandId, request.WorkspaceId, report);
    }

    /// <summary>Maps only safe processor outcomes to HTTP; nonaccepted outcomes disclose no case ID.</summary>
    public static IResult MapResult(CaseRegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            CaseRegistrationStatus.Accepted when HasAcceptedIdentifiers(result) =>
                Results.Json(ToResponse(result), statusCode: StatusCodes.Status201Created),
            CaseRegistrationStatus.Replayed when HasAcceptedIdentifiers(result) =>
                Results.Ok(ToResponse(result)),
            CaseRegistrationStatus.Denied =>
                Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Registration is not authorized."),
            CaseRegistrationStatus.Conflict =>
                Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The registration conflicts with an earlier request."),
            CaseRegistrationStatus.Invalid =>
                Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The registration is invalid."),
            CaseRegistrationStatus.Unavailable =>
                Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Registration is temporarily unavailable."),
            _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Registration is temporarily unavailable.")
        };
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IManagedCaseRegistrationProcessor processor,
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

        if (context.Request.ContentLength is > MaximumRequestBodyBytes)
        {
            return RequestTooLarge();
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
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The registration is invalid.");
        }

        byte[] requestBody;
        try
        {
            requestBody = await ReadBoundedRequestBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestBodyTooLargeException)
        {
            return RequestTooLarge();
        }

        ManagedCaseRegistrationApiRequest? request;
        RegisterManagedCaseRequest applicationRequest;
        try
        {
            request = JsonSerializer.Deserialize<ManagedCaseRegistrationApiRequest>(requestBody, JsonOptions);
            if (request is null)
            {
                throw new ArgumentException("A managed-case request is required.", nameof(request));
            }

            applicationRequest = MapRequest(request);
        }
        catch (Exception exception) when (IsRequestValidationException(exception))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The registration is invalid.");
        }

        try
        {
            var result = await processor.RegisterAsync(actor, applicationRequest, cancellationToken)
                .ConfigureAwait(false);
            if (result.CommandId != applicationRequest.CommandId)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Registration is temporarily unavailable.");
            }

            return MapResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Registration is temporarily unavailable.");
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

    private static IResult RequestTooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The registration request is too large.");

    private static bool IsRequestValidationException(Exception exception) =>
        exception is ArgumentException or InvalidDataException or JsonException or NotSupportedException or InvalidOperationException;

    private static bool HasAcceptedIdentifiers(CaseRegistrationResult result) =>
        result.CaseId is { } caseId && caseId != Guid.Empty &&
        result.CommandId != Guid.Empty &&
        !result.EventIds.IsDefault && result.EventIds.Length == 2 && result.EventIds.All(eventId => eventId != Guid.Empty) &&
        result.AggregateVersion == 2 && result.ReportRevision == 1;

    private static ManagedCaseRegistrationResponse ToResponse(CaseRegistrationResult result) =>
        new(result.CaseId!.Value, result.CommandId, result.EventIds, result.AggregateVersion!.Value, result.ReportRevision!.Value);

    private static CaseReportEventData ToDomainReport(CaseReportV2Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload.Context);
        ArgumentNullException.ThrowIfNull(payload.Context.Occurrence);
        ArgumentNullException.ThrowIfNull(payload.Context.Location);
        ArgumentNullException.ThrowIfNull(payload.Evidence);
        ArgumentNullException.ThrowIfNull(payload.Confirmations);
        ArgumentNullException.ThrowIfNull(payload.Evidence.Observations);

        if (!Enum.IsDefined(payload.Context.CapturePrecision) ||
            !Enum.IsDefined(payload.Context.Location.Source) ||
            !Enum.IsDefined(payload.Context.Location.Precision))
        {
            throw new ArgumentException("The report context contains an unsupported enum value.", nameof(payload));
        }

        var context = new ReportContext(
            ToDomainFact(payload.Context.Occurrence),
            payload.Context.CapturedAtUtc,
            payload.Context.CapturedOffset,
            payload.Context.CapturedZoneId,
            payload.Context.CapturePrecision,
            payload.Context.Location);
        var evidence = new ReportEvidence(
            payload.Evidence.Narrative is null ? null : ToDomainText(payload.Evidence.Narrative),
            payload.Evidence.Observations.Select(ToDomainText),
            payload.Evidence.OriginalAudio is null
                ? null
                : new ReportEvidenceReference(
                    payload.Evidence.OriginalAudio.Reference,
                    ToDomainProvenance(payload.Evidence.OriginalAudio.Provenance)));
        var confirmations = new ReportConfirmations(
            payload.Confirmations.ReportReviewConfirmed,
            payload.Confirmations.OccurrenceCountryCode,
            payload.Confirmations.OccurrenceCountryCatalogueVersion,
            payload.Confirmations.OccurrenceCountryConfirmed,
            payload.Confirmations.ProcessingConfirmed,
            payload.Confirmations.ProcessingPolicyReference,
            payload.Confirmations.ProcessingPolicyVersion,
            payload.Confirmations.RecordingConfirmed,
            payload.Confirmations.RecordingPolicyReference,
            payload.Confirmations.RecordingPolicyVersion);

        return payload.ReportKind switch
        {
            "Human" when payload.Human is not null && payload.Animal is null =>
                new CaseReportEventData.HumanV2(
                    HumanReport.Create(
                        payload.ReportId,
                        context,
                        ToDomainFact(payload.Human.PseudonymousSubjectReference),
                        ToDomainFact(payload.Human.AgeYears),
                        ToDomainFact(payload.Human.ReportedConcern),
                        evidence),
                    confirmations),
            "Animal" when payload.Animal is not null && payload.Human is null =>
                new CaseReportEventData.AnimalV2(
                    AnimalReport.Create(
                        payload.ReportId,
                        context,
                        ToDomainSubjectScope(payload.Animal.SubjectScope),
                        ToDomainFact(payload.Animal.Species),
                        ToDomainFact(payload.Animal.HoldingReference),
                        evidence,
                        ToDomainCount(payload.Animal.AnimalsAtRisk),
                        ToDomainCount(payload.Animal.AnimalsAffected),
                        ToDomainCount(payload.Animal.AnimalsDead)),
                    confirmations),
            _ => throw new ArgumentException("The typed report kind and report fields do not match.", nameof(payload))
        };
    }

    private static ReportedFact<T> ToDomainFact<T>(ReportedFactV2Payload<T>? fact)
    {
        if (fact is null)
        {
            throw new ArgumentException("A reported fact is required.", nameof(fact));
        }

        var provenance = ToDomainProvenance(fact.Provenance);
        return fact.State switch
        {
            ReportedValueV2State.Unknown when !fact.Value.HasValue => ReportedFact<T>.Unknown(provenance),
            ReportedValueV2State.NotRecorded when !fact.Value.HasValue => ReportedFact<T>.NotRecorded(provenance),
            ReportedValueV2State.NotApplicable when !fact.Value.HasValue => ReportedFact<T>.NotApplicable(provenance),
            ReportedValueV2State.Absent when !fact.Value.HasValue => ReportedFact<T>.Absent(provenance),
            ReportedValueV2State.Known when fact.Value.HasValue => ReportedFact<T>.Known(
                fact.Value.Value.Deserialize<T>(JsonOptions) ?? throw new ArgumentException("A known fact requires a value.", nameof(fact)), provenance),
            _ => throw new ArgumentException("The reported fact state and value do not match.", nameof(fact))
        };
    }

    private static ReportedText ToDomainText(ReportedTextV2Payload text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ReportedText(text.Text, ToDomainProvenance(text.Provenance));
    }

    private static ReportProvenance ToDomainProvenance(ReportProvenanceV2Payload? provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var source = provenance.Source switch
        {
            ReportSourceV2Kind.ReporterNarrative => ReportSourceKind.ReporterNarrative,
            ReportSourceV2Kind.ReporterObservation => ReportSourceKind.ReporterObservation,
            ReportSourceV2Kind.OriginalAudio => ReportSourceKind.OriginalAudio,
            ReportSourceV2Kind.WorkerConfirmed => ReportSourceKind.WorkerConfirmed,
            _ => throw new ArgumentException("The report provenance source is not supported.", nameof(provenance))
        };
        return new ReportProvenance(source, provenance.StableReference);
    }

    private static AnimalSubjectScope ToDomainSubjectScope(AnimalSubjectV2Scope scope) => scope switch
    {
        AnimalSubjectV2Scope.Individual => AnimalSubjectScope.Individual,
        AnimalSubjectV2Scope.Herd => AnimalSubjectScope.Herd,
        AnimalSubjectV2Scope.Flock => AnimalSubjectScope.Flock,
        AnimalSubjectV2Scope.OtherGroup => AnimalSubjectScope.OtherGroup,
        _ => throw new ArgumentException("The animal subject scope is not supported.", nameof(scope))
    };

    private static AnimalCount ToDomainCount(AnimalCountV2Payload? count)
    {
        ArgumentNullException.ThrowIfNull(count);
        var state = count.State switch
        {
            ReportedValueV2State.Unknown => ReportedValueState.Unknown,
            ReportedValueV2State.NotRecorded => ReportedValueState.NotRecorded,
            ReportedValueV2State.NotApplicable => ReportedValueState.NotApplicable,
            ReportedValueV2State.Absent => ReportedValueState.Absent,
            ReportedValueV2State.Known => ReportedValueState.Known,
            _ => throw new ArgumentException("The animal count state is not supported.", nameof(count))
        };

        if ((state == ReportedValueState.Known) != count.Value.HasValue)
        {
            throw new ArgumentException("The animal count state and value do not match.", nameof(count));
        }

        var value = state switch
        {
            ReportedValueState.Unknown => ReportedValue<int>.Unknown,
            ReportedValueState.NotRecorded => ReportedValue<int>.NotRecorded,
            ReportedValueState.NotApplicable => ReportedValue<int>.NotApplicable,
            ReportedValueState.Absent => ReportedValue<int>.Absent,
            ReportedValueState.Known => ReportedValue<int>.Known(count.Value!.Value),
            _ => throw new ArgumentException("The animal count state is not supported.", nameof(count))
        };

        return new AnimalCount(
            value,
            ToDomainProvenance(count.Provenance),
            count.PopulationReference,
            count.ObservationPeriod is null ? null : ToDomainFact(count.ObservationPeriod));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class RequestBodyTooLargeException : Exception
    {
    }
}

/// <summary>Wire-only registration request; actor, owner, case, role, and capability IDs are not accepted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManagedCaseRegistrationApiRequest(
    int SchemaVersion,
    Guid CommandId,
    string? WorkspaceId,
    CaseReportV2Payload? Report);

/// <summary>Safe receipt returned only after accepted registration or authorized receipt replay.</summary>
public sealed record ManagedCaseRegistrationResponse(
    Guid CaseId,
    Guid CommandId,
    IReadOnlyList<Guid> EventIds,
    long AggregateVersion,
    long ReportRevision);
