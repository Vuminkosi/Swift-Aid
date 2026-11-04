using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Notifications;
using SwiftAid.Gateway.Notifications;

namespace SwiftAid.Gateway.Controllers.Notifications;

/// <summary>Authenticated push-device lifecycle. Tokens are accepted for storage but never returned.</summary>
[ApiController]
[Authorize(Policy = "SwiftAidStaffApi")]
[Route("api/v1/push/devices")]
public sealed class PushDevicesController : ControllerBase
{
    private const int MaximumTokenLength = 4096;
    private readonly INotificationApiService _notifications;

    public PushDevicesController(INotificationApiService notifications)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    }

    [HttpPost]
    [RequestSizeLimit(8192)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RegisterAsync(
        [FromBody] PushDeviceRegistrationRequest? request,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
        {
            return Unauthorized();
        }

        if (request is null ||
            request.InstallationId == Guid.Empty ||
            !Enum.IsDefined(request.Platform) ||
            string.IsNullOrWhiteSpace(request.Token) ||
            request.Token.Length > MaximumTokenLength ||
            !string.Equals(request.Token, request.Token.Trim(), StringComparison.Ordinal))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The device registration is invalid.");
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
            var result = await _notifications.RegisterDeviceAsync(actor, request, cancellationToken)
                .ConfigureAwait(false);

            return result switch
            {
                PushDeviceRegistrationStatus.Registered => NoContent(),
                PushDeviceRegistrationStatus.Invalid => Problem(statusCode: StatusCodes.Status400BadRequest, title: "The device registration is invalid."),
                PushDeviceRegistrationStatus.NotAuthenticated => Unauthorized(),
                _ => Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Device registration is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Device registration is temporarily unavailable.");
        }
    }

    [HttpDelete("{installationId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RemoveAsync(
        Guid installationId,
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
            var result = await _notifications.RemoveDeviceAsync(actor, installationId, cancellationToken)
                .ConfigureAwait(false);
            return result switch
            {
                PushDeviceRemovalStatus.Removed => NoContent(),
                PushDeviceRemovalStatus.NotFoundOrNotAuthorized => NotFound(),
                PushDeviceRemovalStatus.NotAuthenticated => Unauthorized(),
                _ => Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Device registration is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Device registration is temporarily unavailable.");
        }
    }

    private static async Task<bool> RequiresAntiforgeryAsync(HttpContext context)
    {
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
