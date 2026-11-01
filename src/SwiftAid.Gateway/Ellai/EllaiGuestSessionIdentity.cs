using System.Security.Claims;
using SwiftAid.Application.Authorization;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Per-Blazor-circuit identity for anonymous public-help sessions.
/// It is created by server code only, carries no workspace authority and
/// cannot be supplied through a request field.</summary>
public sealed class EllaiGuestSessionIdentity
{
    private readonly object _gate = new();
    private ClaimsPrincipal? _principal;
    private string? _sessionId;
    private DateTimeOffset _expiresAtUtc;

    public ClaimsPrincipal CreatePrincipal()
    {
        if (TryGetHistoryPrincipal(out var existing)) return existing;
        var actorId = "ellai-guest-" + Guid.NewGuid().ToString("N");
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "0"),
            new Claim("swiftaid_ellai_guest", "true"),
        ], authenticationType: "SwiftAid.EllaiGuestCircuit"));
    }

    public void Bind(string sessionId, ClaimsPrincipal principal, DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(principal);
        if (expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        }

        _ = ActorContext.FromValidatedPrincipal(principal);
        lock (_gate)
        {
            _sessionId = sessionId;
            _principal = principal;
            _expiresAtUtc = expiresAtUtc;
        }
    }

    public bool TryGetPrincipal(string sessionId, out ClaimsPrincipal principal)
    {
        lock (_gate)
        {
            if (_principal is not null
                && _expiresAtUtc > DateTimeOffset.UtcNow
                && string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                principal = _principal;
                return true;
            }

            if (_expiresAtUtc <= DateTimeOffset.UtcNow)
            {
                ClearCore();
            }
        }

        principal = new ClaimsPrincipal(new ClaimsIdentity());
        return false;
    }

    public bool IsGuestPrincipal(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue("swiftaid_ellai_guest"), "true", StringComparison.Ordinal);

    public bool TryGetHistoryPrincipal(out ClaimsPrincipal principal)
    {
        lock (_gate)
        {
            if (_principal is not null && _expiresAtUtc > DateTimeOffset.UtcNow)
            {
                principal = _principal;
                return true;
            }
            ClearCore();
        }
        principal = new ClaimsPrincipal(new ClaimsIdentity());
        return false;
    }

    public void Clear(string sessionId)
    {
        lock (_gate)
        {
            if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                ClearCore();
            }
        }
    }

    private void ClearCore()
    {
        _sessionId = null;
        _principal = null;
        _expiresAtUtc = DateTimeOffset.MinValue;
    }
}
