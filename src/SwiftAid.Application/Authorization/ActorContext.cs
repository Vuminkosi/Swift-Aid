using System.Security.Claims;

namespace SwiftAid.Application.Authorization;

/// <summary>
/// Server-side identity context created from an authenticated principal.
/// It is not a wire DTO and contains no client-selected role or permission.
/// </summary>
public sealed class ActorContext
{
    public const string AccessEpochClaimType = "swiftaid_access_epoch";

    private ActorContext(string actorId, long accessEpoch)
    {
        ActorId = actorId;
        AccessEpoch = accessEpoch;
    }

    public string ActorId { get; }

    /// <summary>A server-issued epoch bound to this authenticated session.</summary>
    public long AccessEpoch { get; }

    /// <summary>
    /// Creates an actor context from the principal validated by server authentication.
    /// Callers must pass the authenticated server principal, never request-body data.
    /// </summary>
    public static ActorContext FromValidatedPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("An authenticated server principal is required.");
        }

        var subjects = principal.Claims
            .Where(claim => claim.Type is ClaimTypes.NameIdentifier or "sub")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (subjects.Length != 1)
        {
            throw new UnauthorizedAccessException("The authenticated principal has no unambiguous subject.");
        }

        var epochClaims = principal.FindAll(AccessEpochClaimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (epochClaims.Length != 1 ||
            !long.TryParse(epochClaims[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var accessEpoch) ||
            accessEpoch < 0)
        {
            throw new UnauthorizedAccessException("The authenticated principal has no valid access epoch.");
        }

        return new ActorContext(subjects[0], accessEpoch);
    }
}
