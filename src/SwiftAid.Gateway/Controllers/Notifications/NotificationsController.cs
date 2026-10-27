using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Gateway.Notifications;

namespace SwiftAid.Gateway.Controllers.Notifications;

/// <summary>Authenticated, recipient-scoped notification feed and opaque change stream.</summary>
[ApiController]
[Authorize(Policy = "SwiftAidStaffApi")]
[Route("api/v1/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(20);
    private const int MaximumPageSize = 100;

    private readonly INotificationApiService _notifications;
    private readonly INotificationChangeSignal _changes;

    public NotificationsController(
        INotificationApiService notifications,
        INotificationChangeSignal changes)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    [HttpGet]
    [ProducesResponseType<NotificationFeedPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] int pageSize = 50,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out var actor))
        {
            return Unauthorized();
        }

        if (pageSize is < 1 or > MaximumPageSize ||
            continuationToken is { Length: > 1024 })
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The notification request is invalid.");
        }

        try
        {
            var page = await _notifications.GetAsync(
                actor,
                pageSize,
                continuationToken,
                cancellationToken).ConfigureAwait(false);
            return Ok(page);
        }
        catch (ArgumentException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The notification request is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Notifications are temporarily unavailable.");
        }
    }

    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> MarkReadAsync(
        Guid id,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken) =>
        MutateAsync(id, dismiss: false, antiforgery, cancellationToken);

    [HttpPost("{id:guid}/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> DismissAsync(
        Guid id,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken) =>
        MutateAsync(id, dismiss: true, antiforgery, cancellationToken);

    [HttpGet("stream")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task StreamAsync(CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _changes.WaitForChangeAsync(
                    actor,
                    StreamHeartbeatInterval,
                    cancellationToken).ConfigureAwait(false);

                if (result == NotificationChangeWaitResult.ActorNoLongerAuthorized)
                {
                    break;
                }

                if (result == NotificationChangeWaitResult.Changed)
                {
                    // Deliberately send an empty event. The client fetches the authorized feed over HTTP.
                    await Response.WriteAsync("event: changed\ndata: {}\n\n", cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await Response.WriteAsync("event: heartbeat\ndata: {}\n\n", cancellationToken)
                        .ConfigureAwait(false);
                }

                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller disconnected or the host is stopping.
        }
        catch (IOException)
        {
            // A disconnected client needs no retry from the server; it reconnects and fetches the feed.
        }
    }

    private async Task<IActionResult> MutateAsync(
        Guid id,
        bool dismiss,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
        {
            return Unauthorized();
        }

        if (await RequiresAntiforgeryAsync(HttpContext).ConfigureAwait(false))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(HttpContext).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Request verification failed.");
            }
        }

        try
        {
            var result = dismiss
                ? await _notifications.DismissAsync(actor, id, cancellationToken).ConfigureAwait(false)
                : await _notifications.MarkReadAsync(actor, id, cancellationToken).ConfigureAwait(false);

            return result switch
            {
                NotificationMutationStatus.Applied => NoContent(),
                NotificationMutationStatus.NotFoundOrNotAuthorized => NotFound(),
                _ => Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Notifications are temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Notifications are temporarily unavailable.");
        }
    }

    private static async Task<bool> RequiresAntiforgeryAsync(HttpContext context)
    {
        // Cookie-authenticated browser requests require a CSRF token. Native bearer clients do not.
        var cookieAuthentication = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme)
            .ConfigureAwait(false);
        return cookieAuthentication.Succeeded;
    }

    private bool TryGetActor(out ActorContext actor)
    {
        actor = null!;
        if (User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        try
        {
            actor = ActorContext.FromValidatedPrincipal(User);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
