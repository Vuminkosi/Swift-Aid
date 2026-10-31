using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>Marks officer-only session routes that require a trusted scoped
/// authorization capability, which is not currently available in Gateway.</summary>
public sealed record CommunitySessionScopedAuthorizationRequiredMetadata;

/// <summary>Maps community-session officer command endpoints. These routes fail
/// closed until a trusted scoped-staff authorization capability is available.</summary>
/// <remarks>
/// Every command carries the officer-provided expected version; a stale version fails
/// with 409 so a retry re-reads before writing. State-rule violations (for example a
/// past start or a missing self-review acknowledgement) fail with 422. The scoped
/// authorization metadata gate returns a generic 404 before command services are called.
/// </remarks>
public static class CommunitySessionEndpoints
{
    private const int MaximumRequestBodyBytes = 64 * 1024;

    public static void MapCommunitySessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schemes = string.Join(
            ",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        var group = endpoints.MapGroup("/api/v1/sessions");
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.GetEndpoint()?.Metadata
                .GetMetadata<CommunitySessionScopedAuthorizationRequiredMetadata>() is not null)
            {
                // Authentication alone does not establish officer authority.
                // Return the same generic denial regardless of session identity.
                return Results.NotFound();
            }

            return await next(context);
        });
        group.MapPost("/", CreateDraftAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new CommunitySessionScopedAuthorizationRequiredMetadata())
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("CreateCommunitySessionDraft")
            .Accepts<CreateSessionApiRequest>("application/json")
            .Produces<SessionCommandResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapPut("/{sessionId:guid}", UpdateAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new CommunitySessionScopedAuthorizationRequiredMetadata())
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("UpdateCommunitySession")
            .Accepts<UpdateSessionApiRequest>("application/json")
            .Produces<SessionCommandResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/{sessionId:guid}/review", ReviewAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new CommunitySessionScopedAuthorizationRequiredMetadata())
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("ReviewCommunitySession")
            .Accepts<ReviewSessionApiRequest>("application/json")
            .Produces<SessionCommandResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/{sessionId:guid}/schedule", ScheduleAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new CommunitySessionScopedAuthorizationRequiredMetadata())
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("ScheduleCommunitySession")
            .Accepts<ScheduleSessionApiRequest>("application/json")
            .Produces<SessionCommandResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/{sessionId:guid}/cancel", CancelAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new CommunitySessionScopedAuthorizationRequiredMetadata())
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("CancelCommunitySession")
            .Accepts<CancelSessionApiRequest>("application/json")
            .Produces<SessionCommandResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/{sessionId:guid}/complete", CompleteAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithMetadata(new CommunitySessionScopedAuthorizationRequiredMetadata())
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBodyBytes))
            .WithName("CompleteCommunitySession")
            .Accepts<CompleteSessionApiRequest>("application/json")
            .Produces<SessionCommandResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Maps a session command outcome to HTTP.</summary>
    public static IResult MapResult(SessionCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SessionId == Guid.Empty)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The community session is temporarily unavailable.");
        }

        return Results.Json(result, options: JsonOptions, statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// Maps service failures to HTTP. Validation failures are 400; stale expected
    /// versions are 409; other lifecycle violations are 422; denial is a generic
    /// 404 so the response never reveals whether a session exists.
    /// </summary>
    public static IResult MapCommandException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            ArgumentException => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The community session command is invalid."),
            InvalidOperationException state when IsVersionConflict(state) => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The community session changed before the command was recorded."),
            InvalidOperationException => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The community session cannot make that change in its current state."),
            UnauthorizedAccessException => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "The community session was not found."),
            _ => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The community session is temporarily unavailable.")
        };
    }

    private static async Task<IResult> CreateDraftAsync(
        HttpContext context,
        CreateSessionApiRequest? request,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var actor = TryGetActor(context);
        if (actor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The community session was not found.");
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

        SessionCommand command;
        try
        {
            command = MapCreateRequest(request);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The community session command is invalid.");
        }

        try
        {
            var result = await service.ExecuteAsync(command, actor.ActorId, cancellationToken).ConfigureAwait(false);
            return MapResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapCommandException(exception);
        }
    }

    private static Task<IResult> UpdateAsync(
        HttpContext context,
        Guid sessionId,
        UpdateSessionApiRequest? request,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken) =>
        ExecuteSessionCommandAsync(context, sessionId, request, MapUpdateRequest, antiforgery, cancellationToken);

    private static Task<IResult> ReviewAsync(
        HttpContext context,
        Guid sessionId,
        ReviewSessionApiRequest? request,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken) =>
        ExecuteSessionCommandAsync(context, sessionId, request, MapReviewRequest, antiforgery, cancellationToken);

    private static Task<IResult> ScheduleAsync(
        HttpContext context,
        Guid sessionId,
        ScheduleSessionApiRequest? request,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken) =>
        ExecuteSessionCommandAsync(context, sessionId, request, MapScheduleRequest, antiforgery, cancellationToken);

    private static Task<IResult> CancelAsync(
        HttpContext context,
        Guid sessionId,
        CancelSessionApiRequest? request,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken) =>
        ExecuteSessionCommandAsync(context, sessionId, request, MapCancelRequest, antiforgery, cancellationToken);

    private static Task<IResult> CompleteAsync(
        HttpContext context,
        Guid sessionId,
        CompleteSessionApiRequest? request,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken) =>
        ExecuteSessionCommandAsync(context, sessionId, request, MapCompleteRequest, antiforgery, cancellationToken);

    private static async Task<IResult> ExecuteSessionCommandAsync<TRequest>(
        HttpContext context,
        Guid sessionId,
        TRequest? request,
        Func<Guid, TRequest, SessionCommand> map,
        [FromServices] IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var actor = TryGetActor(context);
        if (actor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (sessionId == Guid.Empty)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The community session identifier is invalid.");
        }

        var service = context.RequestServices.GetService<ICommunitySessionService>();
        if (service is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The community session was not found.");
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

        SessionCommand command;
        try
        {
            command = map(sessionId, request!);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The community session command is invalid.");
        }

        try
        {
            var result = await service.ExecuteAsync(command, actor.ActorId, cancellationToken).ConfigureAwait(false);
            return MapResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapCommandException(exception);
        }
    }

    /// <summary>Converts the create-draft wire request to a trusted application command.</summary>
    public static SessionCommand MapCreateRequest(CreateSessionApiRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CreateSessionDraftCommand(
            Guid.NewGuid(),
            RequireText(request.Title, nameof(request.Title)),
            RequireText(request.Summary, nameof(request.Summary)),
            RequireText(request.AudienceDescription, nameof(request.AudienceDescription)),
            RequireTags(request.TopicTags),
            RequireText(request.CountryCode, nameof(request.CountryCode)),
            RequireText(request.RegionId, nameof(request.RegionId)),
            RequireText(request.Sector, nameof(request.Sector)),
            RequireText(request.Presenter, nameof(request.Presenter)),
            RequireText(request.Language, nameof(request.Language)),
            RequireStart(request.StartUtc),
            RequireText(request.TimeZoneId, nameof(request.TimeZoneId)),
            RequireDuration(request.DurationMinutes),
            RequireCapacity(request.Capacity),
            RequireMaterials(request.Materials),
            RequireVisibility(request.Visibility),
            RequireChannelType(request.ChannelType),
            RequireChannelLink(request.ChannelType, request.ChannelLink),
            RequireReminderLeads(request.ReminderLeadHours),
            RequireText(request.Reason, nameof(request.Reason)),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Converts the update wire request to a trusted application command.</summary>
    public static SessionCommand MapUpdateRequest(Guid sessionId, UpdateSessionApiRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }

        return new UpdateSessionCommand(
            sessionId,
            RequireExpectedVersion(request.ExpectedVersion),
            RequireText(request.Title, nameof(request.Title)),
            RequireText(request.Summary, nameof(request.Summary)),
            RequireText(request.AudienceDescription, nameof(request.AudienceDescription)),
            RequireTags(request.TopicTags),
            RequireText(request.Presenter, nameof(request.Presenter)),
            RequireText(request.Language, nameof(request.Language)),
            RequireStart(request.StartUtc),
            RequireDuration(request.DurationMinutes),
            RequireCapacity(request.Capacity),
            RequireMaterials(request.Materials),
            RequireVisibility(request.Visibility),
            RequireChannelType(request.ChannelType),
            RequireChannelLink(request.ChannelType, request.ChannelLink),
            RequireReminderLeads(request.ReminderLeadHours),
            RequireText(request.Reason, nameof(request.Reason)),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Converts the review wire request to a trusted application command.</summary>
    public static SessionCommand MapReviewRequest(Guid sessionId, ReviewSessionApiRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }

        return new ReviewSessionCommand(
            sessionId,
            RequireExpectedVersion(request.ExpectedVersion),
            request.AcknowledgeSelfReview,
            RequireText(request.Reason, nameof(request.Reason)),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Converts the schedule wire request to a trusted application command.</summary>
    public static SessionCommand MapScheduleRequest(Guid sessionId, ScheduleSessionApiRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }

        return new ScheduleSessionCommand(
            sessionId,
            RequireExpectedVersion(request.ExpectedVersion),
            RequireText(request.Reason, nameof(request.Reason)),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Converts the cancel wire request to a trusted application command.</summary>
    public static SessionCommand MapCancelRequest(Guid sessionId, CancelSessionApiRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }

        return new CancelSessionCommand(
            sessionId,
            RequireExpectedVersion(request.ExpectedVersion),
            RequireText(request.CancellationReason, nameof(request.CancellationReason)),
            RequireText(request.Reason, nameof(request.Reason)),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Converts the complete wire request to a trusted application command.</summary>
    public static SessionCommand MapCompleteRequest(Guid sessionId, CompleteSessionApiRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }

        return new CompleteSessionCommand(
            sessionId,
            RequireExpectedVersion(request.ExpectedVersion),
            RequireText(request.Reason, nameof(request.Reason)),
            DateTimeOffset.UtcNow);
    }

    private static bool IsVersionConflict(InvalidOperationException exception)
    {
        var message = exception.Message;
        return message.Contains("version", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("concurrency", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("stale", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("changed before", StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", name);
        }

        return value;
    }

    private static long RequireExpectedVersion(long expectedVersion)
    {
        if (expectedVersion < 1)
        {
            throw new ArgumentException("A non-creation session command requires an existing version.", nameof(expectedVersion));
        }

        return expectedVersion;
    }

    private static DateTimeOffset RequireStart(DateTimeOffset? startUtc)
    {
        if (startUtc is null || startUtc.Value == default)
        {
            throw new ArgumentException("A session start is required.", nameof(startUtc));
        }

        return startUtc.Value;
    }

    private static int RequireDuration(int durationMinutes)
    {
        if (durationMinutes < 1 || durationMinutes > 1440)
        {
            throw new ArgumentException("Duration must be between 1 and 1440 minutes.", nameof(durationMinutes));
        }

        return durationMinutes;
    }

    private static int? RequireCapacity(int? capacity)
    {
        if (capacity is not null && capacity < 1)
        {
            throw new ArgumentException("Capacity must be positive when set.", nameof(capacity));
        }

        return capacity;
    }

    private static IReadOnlyList<string> RequireTags(IReadOnlyList<string>? tags)
    {
        if (tags is null)
        {
            throw new ArgumentException("Topic tags are required.", nameof(tags));
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("Topic tags must not be blank.", nameof(tags));
            }
        }

        return tags;
    }

    private static IReadOnlyList<SessionMaterialRef> RequireMaterials(IReadOnlyList<SessionMaterialApiRef>? materials)
    {
        if (materials is null || materials.Count == 0)
        {
            throw new ArgumentException("At least one source-linked material is required.", nameof(materials));
        }

        var bound = new List<SessionMaterialRef>(materials.Count);
        foreach (var material in materials)
        {
            if (material is null ||
                string.IsNullOrWhiteSpace(material.MaterialId) ||
                string.IsNullOrWhiteSpace(material.SourceVersion))
            {
                throw new ArgumentException("Materials require an identifier and a source version.", nameof(materials));
            }

            bound.Add(new SessionMaterialRef(material.MaterialId, material.SourceVersion));
        }

        return bound;
    }

    private static SessionVisibility RequireVisibility(string? visibility)
    {
        if (!Enum.TryParse<SessionVisibility>(visibility, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new ArgumentException("The session visibility is not supported.", nameof(visibility));
        }

        return parsed;
    }

    private static SessionChannelType RequireChannelType(string? channelType)
    {
        if (!Enum.TryParse<SessionChannelType>(channelType, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new ArgumentException("The session channel type is not supported.", nameof(channelType));
        }

        return parsed;
    }

    private static string? RequireChannelLink(string? channelTypeText, string? channelLink)
    {
        var channelType = RequireChannelType(channelTypeText);
        if (channelType == SessionChannelType.None)
        {
            if (channelLink is not null)
            {
                throw new ArgumentException("A channel link requires a channel type.", nameof(channelLink));
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(channelLink))
        {
            throw new ArgumentException("A channel link is required for the selected channel type.", nameof(channelLink));
        }

        if (!Uri.TryCreate(channelLink, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("A channel link must be an absolute http(s) URL.", nameof(channelLink));
        }

        return channelLink;
    }

    private static IReadOnlyList<int> RequireReminderLeads(IReadOnlyList<int>? leads)
    {
        if (leads is null)
        {
            throw new ArgumentException("Reminder lead hours are required.", nameof(leads));
        }

        if (leads.Distinct().Count() != leads.Count)
        {
            throw new ArgumentException("Reminder lead hours must be distinct.", nameof(leads));
        }

        foreach (var lead in leads)
        {
            if (lead < 1 || lead > 168)
            {
                throw new ArgumentException("Reminder lead hours must be between 1 and 168.", nameof(leads));
            }
        }

        return leads;
    }

    private static ActorContext? TryGetActor(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return ActorContext.FromValidatedPrincipal(context.User);
    }

    private static async Task<bool> RequiresAntiforgeryAsync(HttpContext context)
    {
        // Authenticate against the configured server schemes instead of trusting request headers
        // or caller-provided fields to distinguish browser and native clients. If a valid browser
        // cookie is also present, keep the CSRF check even when a Bearer [REDACTED] is supplied.
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>Wire request to create a community-session draft. The server assigns the session identifier.</summary>
public sealed record CreateSessionApiRequest(
    string? Title,
    string? Summary,
    string? AudienceDescription,
    IReadOnlyList<string>? TopicTags,
    string? CountryCode,
    string? RegionId,
    string? Sector,
    string? Presenter,
    string? Language,
    DateTimeOffset? StartUtc,
    string? TimeZoneId,
    int DurationMinutes,
    int? Capacity,
    IReadOnlyList<SessionMaterialApiRef>? Materials,
    string? Visibility,
    string? ChannelType,
    string? ChannelLink,
    IReadOnlyList<int>? ReminderLeadHours,
    string? Reason);

/// <summary>Wire request to edit a draft, reviewed, or scheduled session.</summary>
public sealed record UpdateSessionApiRequest(
    long ExpectedVersion,
    string? Title,
    string? Summary,
    string? AudienceDescription,
    IReadOnlyList<string>? TopicTags,
    string? Presenter,
    string? Language,
    DateTimeOffset? StartUtc,
    int DurationMinutes,
    int? Capacity,
    IReadOnlyList<SessionMaterialApiRef>? Materials,
    string? Visibility,
    string? ChannelType,
    string? ChannelLink,
    IReadOnlyList<int>? ReminderLeadHours,
    string? Reason);

/// <summary>Wire request to record review. A creator publishing their own draft sets AcknowledgeSelfReview.</summary>
public sealed record ReviewSessionApiRequest(
    long ExpectedVersion,
    bool AcknowledgeSelfReview,
    string? Reason);

/// <summary>Wire request to publish a reviewed session.</summary>
public sealed record ScheduleSessionApiRequest(
    long ExpectedVersion,
    string? Reason);

/// <summary>Wire request to cancel a reviewed or scheduled session and suppress pending reminders.</summary>
public sealed record CancelSessionApiRequest(
    long ExpectedVersion,
    string? CancellationReason,
    string? Reason);

/// <summary>Wire request to complete a scheduled session after it starts.</summary>
public sealed record CompleteSessionApiRequest(
    long ExpectedVersion,
    string? Reason);

/// <summary>Wire reference to a human-authored source card and its bound version.</summary>
public sealed record SessionMaterialApiRef(
    string? MaterialId,
    string? SourceVersion);
